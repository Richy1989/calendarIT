using CalendarIT.Application.Auth;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Auth;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CalendarIT.Tests;

/// <summary>
/// A refresh rotates its token in two writes (claim the old one, store the new one). A sign-out
/// landing between them, or the second write failing, must not leave the session in the wrong
/// state. Runs on a real SQLite file (separate connections, as in the app) and — when
/// TEST_POSTGRES_CONNECTION names a server the test may create databases on — on PostgreSQL.
/// </summary>
public sealed class TokenRotationRaceTests
{
    private const string Email = "race@test.com";
    private const string Password = "Original1#pass";

    public static TheoryData<string> Providers()
    {
        var data = new TheoryData<string> { "sqlite" };
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")))
        {
            data.Add("postgres");
        }
        return data;
    }

    /// <summary>Holds (or fails) the write that stores a rotated-in refresh token.</summary>
    private sealed class RotationGate : SaveChangesInterceptor
    {
        public TaskCompletionSource Paused { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Armed { get; set; }
        public bool Fail { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var storesToken = eventData.Context!.ChangeTracker.Entries<RefreshToken>().Any(e => e.State == EntityState.Added);
            if (Armed && storesToken)
            {
                Armed = false;
                if (Fail)
                {
                    throw new DbUpdateException("Simulated failure while storing the new refresh token.");
                }
                Paused.TrySetResult();
                await Release.Task;
            }
            return result;
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly string? _cleanup;
        private readonly string? _postgresAdmin;
        public RotationGate Gate { get; } = new();
        public IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions());

        public Harness(string provider)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDataProtection();
            if (provider == "postgres")
            {
                var admin = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION"));
                _postgresAdmin = admin.ConnectionString;
                var db = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = $"calendarit_race_{Guid.NewGuid():N}" };
                _cleanup = db.Database;
                services.AddDbContext<AppDbContext>(o => o.UseNpgsql(db.ConnectionString).AddInterceptors(Gate));
            }
            else
            {
                var file = Path.Combine(Path.GetTempPath(), $"calendarit-race-{Guid.NewGuid():N}.db");
                _cleanup = file;
                services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={file};Pooling=False").AddInterceptors(Gate));
            }
            services.AddIdentityCore<ApplicationUser>(o => o.User.RequireUniqueEmail = true)
                .AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<AppDbContext>()
                .AddTokenProvider<DataProtectorTokenProvider<ApplicationUser>>(TokenOptions.DefaultProvider);
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        }

        /// <summary>A service in its own scope (and so its own connection), as each request has.</summary>
        public AuthService Auth()
        {
            var scope = _services.CreateScope();
            return new AuthService(
                scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
                scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                new TokenService(Options.Create(new JwtOptions
                {
                    SigningKey = "test-signing-key-that-is-long-enough-32+", Issuer = "calendarit", Audience = "calendarit",
                }), TimeProvider.System),
                TimeProvider.System, new FakePasswordResetMailer(), Options.Create(new AuthOptions()),
                NullLogger<AuthService>.Instance, Cache);
        }

        public async Task<List<RefreshToken>> TokensAsync()
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().RefreshTokens.AsNoTracking().ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            using (var scope = _services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureDeletedAsync();
            }
            await _services.DisposeAsync();
            Cache.Dispose();
            if (_postgresAdmin is null && _cleanup is not null)
            {
                File.Delete(_cleanup);
            }
        }
    }

    private static async Task<AuthTokens> RegisterAsync(Harness h)
    {
        var result = await h.Auth().RegisterAsync(new RegisterRequest { Email = Email, Password = Password });
        Assert.True(result.Succeeded);
        return result.Tokens!;
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SignOut_DuringARotation_LeavesNoLiveToken(string provider)
    {
        await using var h = new Harness(provider);
        var tokens = await RegisterAsync(h);

        h.Gate.Armed = true;
        var refresh = Task.Run(() => h.Auth().RefreshAsync(new RefreshTokenRequest { RefreshToken = tokens.RefreshToken }));
        await h.Gate.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10)); // old token claimed, new one not stored yet
        var logout = Task.Run(() => h.Auth().LogoutAsync(new LogoutRequest { RefreshToken = tokens.RefreshToken }));

        // The sign-out waits for the rotation to finish instead of slipping in between its writes.
        var first = await Task.WhenAny(logout, Task.Delay(500));
        Assert.NotSame(logout, first);
        h.Gate.Release.TrySetResult();
        var refreshed = await refresh.WaitAsync(TimeSpan.FromSeconds(30));
        await logout.WaitAsync(TimeSpan.FromSeconds(30));

        // Signed out means signed out: no token of the session is live, the rotated-in one included.
        Assert.True(refreshed.Succeeded);
        var all = await h.TokensAsync();
        Assert.Equal(2, all.Count);
        Assert.All(all, t => Assert.NotNull(t.RevokedAt));
        Assert.False((await h.Auth().RefreshAsync(new RefreshTokenRequest { RefreshToken = refreshed.Tokens!.RefreshToken })).Succeeded);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AFailedRotation_LeavesTheOldTokenUsable(string provider)
    {
        await using var h = new Harness(provider);
        var tokens = await RegisterAsync(h);

        h.Gate.Armed = true;
        h.Gate.Fail = true;
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            h.Auth().RefreshAsync(new RefreshTokenRequest { RefreshToken = tokens.RefreshToken }));

        // Nothing half-done: the old token is still the session's one live token...
        var single = Assert.Single(await h.TokensAsync());
        Assert.Null(single.RevokedAt);
        Assert.Null(single.ReplacedByTokenHash);

        // ...so the retry works instead of tripping reuse detection (which signs out every device).
        var retried = await h.Auth().RefreshAsync(new RefreshTokenRequest { RefreshToken = tokens.RefreshToken });
        Assert.True(retried.Succeeded);
        Assert.Single(await h.TokensAsync(), t => t.RevokedAt is null);
    }
}
