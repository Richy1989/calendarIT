using CalendarIT.Application.Auth;
using CalendarIT.CalDav;
using CalendarIT.Infrastructure.Auth;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CalendarIT.Tests;

/// <summary>
/// Sessions: each sign-in is one, a refresh keeps it, the user can see and end them, and ending
/// one actually ends it. Plus the refresh race that used to sign people out of every device.
/// </summary>
public sealed class SessionTests : IDisposable
{
    private const string Email = "owner@test.com";
    private const string Password = "Original1#pass";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly MutableClock _clock = new(DateTimeOffset.UtcNow);
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly CredentialEpochs _epochs = new();
    private readonly FakePasswordResetMailer _mailer = new();
    private readonly List<IServiceScope> _scopes = [];

    public SessionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentityCore<ApplicationUser>(o => o.User.RequireUniqueEmail = true)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddTokenProvider<DataProtectorTokenProvider<ApplicationUser>>(TokenOptions.DefaultProvider);
        _services = services.BuildServiceProvider();
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }
        _services.Dispose();
        _cache.Dispose();
        _connection.Dispose();
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>One service per call, each with its own scope — the way requests get them.</summary>
    private AuthService Auth()
    {
        var scope = _services.CreateScope();
        _scopes.Add(scope);
        return new AuthService(
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            new TokenService(Options.Create(new JwtOptions
            {
                SigningKey = "test-signing-key-that-is-long-enough-32+", Issuer = "calendarit", Audience = "calendarit",
            }), _clock),
            _clock, _mailer, Options.Create(new AuthOptions()), NullLogger<AuthService>.Instance, _cache, _epochs);
    }

    private SessionValidator Validator()
    {
        var scope = _services.CreateScope();
        _scopes.Add(scope);
        return new SessionValidator(_cache, scope.ServiceProvider.GetRequiredService<AppDbContext>(), _clock);
    }

    private async Task<(Guid UserId, AuthTokens Tokens)> RegisterAsync()
    {
        var result = await Auth().RegisterAsync(new RegisterRequest { Email = Email, Password = Password },
            new AuthClient("Firefox on Linux", "203.0.113.7"));
        Assert.True(result.Succeeded);
        using var scope = _services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(Email);
        return (user!.Id, result.Tokens!);
    }

    private async Task<AuthTokens> LoginAsync(string device)
    {
        var result = await Auth().LoginAsync(new LoginRequest { Email = Email, Password = Password }, new AuthClient(device, "198.51.100.1"));
        Assert.True(result.Succeeded);
        return result.Tokens!;
    }

    private async Task<Guid> SessionOfAsync(AuthTokens tokens)
    {
        using var scope = _services.CreateScope();
        var hash = new TokenService(Options.Create(new JwtOptions { SigningKey = "x" }), _clock).HashRefreshToken(tokens.RefreshToken);
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().RefreshTokens.SingleAsync(t => t.TokenHash == hash)).SessionId;
    }

    private Task<AuthResult> RefreshAsync(AuthTokens tokens) =>
        Auth().RefreshAsync(new RefreshTokenRequest { RefreshToken = tokens.RefreshToken });

    [Fact]
    public async Task EachSignIn_IsOneSession_ThatSurvivesRefresh()
    {
        var (userId, first) = await RegisterAsync();
        var second = await LoginAsync("Safari on iPhone");
        var refreshed = await RefreshAsync(second);
        Assert.True(refreshed.Succeeded);

        var sessions = await Auth().ListSessionsAsync(userId, await SessionOfAsync(refreshed.Tokens!));

        Assert.Equal(2, sessions.Count);
        Assert.Equal("Safari on iPhone", sessions[0].UserAgent);
        Assert.True(sessions[0].Current);
        Assert.Equal(await SessionOfAsync(first), sessions[1].Id);
    }

    [Fact]
    public async Task TwoTabsRefreshingAtOnce_DoNotSignTheUserOut()
    {
        var (_, tokens) = await RegisterAsync();

        var winner = await RefreshAsync(tokens);
        var loser = await RefreshAsync(tokens); // the other tab, a moment later, same token

        Assert.True(winner.Succeeded);
        Assert.False(loser.Succeeded);
        // The winning tab's new token still works: the duplicate wasn't treated as theft.
        Assert.True((await RefreshAsync(winner.Tokens!)).Succeeded);
    }

    [Fact]
    public async Task ReplayingAnOldToken_LaterOn_StillRevokesEverything()
    {
        var (_, tokens) = await RegisterAsync();
        var current = (await RefreshAsync(tokens)).Tokens!;

        _clock.Now += TimeSpan.FromMinutes(5); // long after any tab race
        Assert.False((await RefreshAsync(tokens)).Succeeded);

        Assert.False((await RefreshAsync(current)).Succeeded);
    }

    [Fact]
    public async Task ConcurrentRefreshes_OnlyOneWins()
    {
        var (userId, tokens) = await RegisterAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => RefreshAsync(tokens)));

        Assert.Single(results, r => r.Succeeded);
        Assert.Single(await Auth().ListSessionsAsync(userId, null));
    }

    [Fact]
    public async Task RevokingASession_EndsItsRefreshAndAccessAtOnce()
    {
        var (userId, tokens) = await RegisterAsync();
        var sessionId = await SessionOfAsync(tokens);
        Assert.True(await Validator().IsActiveAsync(sessionId, default)); // cached as active

        Assert.True(await Auth().RevokeSessionAsync(userId, sessionId));

        Assert.False(await Validator().IsActiveAsync(sessionId, default));
        Assert.False((await RefreshAsync(tokens)).Succeeded);
    }

    [Fact]
    public async Task SigningOutOthers_KeepsThisSession()
    {
        var (userId, mine) = await RegisterAsync();
        var phone = await LoginAsync("phone");
        var laptop = await LoginAsync("laptop");

        Assert.Equal(2, await Auth().RevokeSessionsAsync(userId, await SessionOfAsync(mine)));

        Assert.True((await RefreshAsync(mine)).Succeeded);
        Assert.False((await RefreshAsync(phone)).Succeeded);
        Assert.False((await RefreshAsync(laptop)).Succeeded);
    }

    [Fact]
    public async Task Logout_EndsTheSession()
    {
        var (userId, tokens) = await RegisterAsync();

        await Auth().LogoutAsync(new LogoutRequest { RefreshToken = tokens.RefreshToken });

        Assert.Empty(await Auth().ListSessionsAsync(userId, null));
        Assert.False(await Validator().IsActiveAsync(await SessionOfAsync(tokens), default));
    }

    [Fact]
    public async Task ChangingThePassword_RetiresCachedCalDavLogins()
    {
        var (userId, _) = await RegisterAsync();
        var davCache = new CalDavCredentialCache(_cache, _epochs);
        davCache.Store(Email, Password, new CalDavCredentialCache.CachedPrincipal(userId, Email));
        Assert.True(davCache.TryGet(Email, Password, out _));

        var changed = await Auth().ChangePasswordAsync(userId,
            new ChangePasswordRequest { CurrentPassword = Password, NewPassword = "Replaced2#pass" });

        Assert.True(changed.Succeeded);
        Assert.False(davCache.TryGet(Email, Password, out _));
    }

    [Fact]
    public async Task ResetMails_AreRateLimitedPerAccount()
    {
        await RegisterAsync();
        var request = new ForgotPasswordRequest { Email = Email };

        await Auth().RequestPasswordResetAsync(request, "https://cal.example.com");
        await Auth().RequestPasswordResetAsync(request, "https://cal.example.com");
        Assert.Single(_mailer.Sent);
    }

    [Fact]
    public void CalDavThrottle_BlocksAnAddressAfterTooManyFailures()
    {
        var throttle = new CalDavFailureThrottle(_cache);
        for (var i = 0; i < CalDavFailureThrottle.MaxFailures - 1; i++)
        {
            throttle.RecordFailure("203.0.113.9");
        }
        Assert.False(throttle.IsBlocked("203.0.113.9"));
        throttle.RecordFailure("203.0.113.9");
        Assert.True(throttle.IsBlocked("203.0.113.9"));
        Assert.False(throttle.IsBlocked("203.0.113.10"));
    }
}
