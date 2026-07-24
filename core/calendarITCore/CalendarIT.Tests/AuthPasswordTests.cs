using System.Web;
using CalendarIT.Application.Auth;
using CalendarIT.Infrastructure.Auth;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CalendarIT.Tests;

/// <summary>
/// Password change, recovery, and the registration switch — driven through a real Identity stack
/// (real hasher, real Data-Protection reset tokens) because a fake would prove nothing about the
/// part that matters: that an emailed link works exactly once and dies when used.
/// </summary>
public sealed class AuthPasswordTests : IDisposable
{
    private const string Email = "owner@test.com";
    private const string OriginalPassword = "Original1#pass";
    private const string NewPassword = "Replaced2#pass";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly FakePasswordResetMailer _mailer = new();
    private readonly List<IServiceScope> _scopes = [];

    public AuthPasswordTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 8;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 10;
            })
            // Roles are part of the real registration too — IssueTokensAsync reads them into the
            // JWT, so the store has to support them here as well.
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddTokenProvider<DataProtectorTokenProvider<ApplicationUser>>(TokenOptions.DefaultProvider);

        _services = services.BuildServiceProvider();
        _db = _services.GetRequiredService<AppDbContext>();
        _db.Database.EnsureCreated();
        _users = _services.GetRequiredService<UserManager<ApplicationUser>>();
    }

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }
        _services.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// A service backed by its own scope, the way each HTTP request gets one. That matters here:
    /// session revocation runs as a direct UPDATE, which a DbContext that already has those rows
    /// tracked would not see — sharing one context across calls would test a situation production
    /// never puts itself in.
    /// </summary>
    private AuthService NewService(bool disableRegistration = false)
    {
        var scope = _services.CreateScope();
        _scopes.Add(scope);
        return new AuthService(
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            new TokenService(Options.Create(new JwtOptions
            {
                SigningKey = "test-signing-key-that-is-long-enough-32+",
                Issuer = "calendarit",
                Audience = "calendarit",
            }), TimeProvider.System),
            TimeProvider.System,
            _mailer,
            Options.Create(new AuthOptions { DisableRegistration = disableRegistration }),
            NullLogger<AuthService>.Instance);
    }

    private async Task<Guid> RegisterAsync()
    {
        var result = await NewService().RegisterAsync(new RegisterRequest { Email = Email, Password = OriginalPassword });
        Assert.True(result.Succeeded);
        return (await _users.FindByEmailAsync(Email))!.Id;
    }

    private async Task<bool> CanLogInAsync(string password) =>
        (await NewService().LoginAsync(new LoginRequest { Email = Email, Password = password })).Succeeded;

    /// <summary>The token as the SPA would send it back, pulled out of the emailed link.</summary>
    private string TokenFromLink()
    {
        var query = HttpUtility.ParseQueryString(new Uri(_mailer.LastLink!).Query);
        return query["token"]!;
    }

    // ---------------------------------------------------------------- change password

    [Fact]
    public async Task ChangePassword_WithTheCurrentOne_Works()
    {
        var userId = await RegisterAsync();

        var result = await NewService().ChangePasswordAsync(userId, new ChangePasswordRequest
        {
            CurrentPassword = OriginalPassword,
            NewPassword = NewPassword,
        });

        Assert.True(result.Succeeded);
        Assert.True(await CanLogInAsync(NewPassword));
        Assert.False(await CanLogInAsync(OriginalPassword));
    }

    [Fact]
    public async Task ChangePassword_WithTheWrongCurrentOne_IsRefused()
    {
        var userId = await RegisterAsync();

        var result = await NewService().ChangePasswordAsync(userId, new ChangePasswordRequest
        {
            CurrentPassword = "not-the-password",
            NewPassword = NewPassword,
        });

        Assert.False(result.Succeeded);
        Assert.True(await CanLogInAsync(OriginalPassword)); // unchanged
    }

    [Fact]
    public async Task ChangePassword_RevokesExistingSessions()
    {
        var userId = await RegisterAsync();
        var session = await NewService().LoginAsync(new LoginRequest { Email = Email, Password = OriginalPassword });
        var refreshToken = session.Tokens!.RefreshToken;

        await NewService().ChangePasswordAsync(userId, new ChangePasswordRequest
        {
            CurrentPassword = OriginalPassword,
            NewPassword = NewPassword,
        });

        // The old session must not survive: ending someone else's access is the point.
        var refreshed = await NewService().RefreshAsync(new RefreshTokenRequest { RefreshToken = refreshToken });
        Assert.False(refreshed.Succeeded);
    }

    // ---------------------------------------------------------------- reset

    [Fact]
    public async Task ForgotPassword_EmailsALinkForARegisteredAddress()
    {
        var userId = await RegisterAsync();

        await NewService().RequestPasswordResetAsync(new ForgotPasswordRequest { Email = Email }, "https://cal.example.com");

        var sent = Assert.Single(_mailer.Sent);
        Assert.Equal(userId, sent.UserId);
        Assert.Equal(Email, sent.To);
        Assert.StartsWith("https://cal.example.com/reset-password?", sent.Link);
    }

    [Fact]
    public async Task ForgotPassword_ForAnUnknownAddress_SaysAndDoesNothing()
    {
        await RegisterAsync();

        await NewService().RequestPasswordResetAsync(
            new ForgotPasswordRequest { Email = "nobody@test.com" }, "https://cal.example.com");

        // No mail, no throw — the caller can't tell this address from a registered one.
        Assert.Empty(_mailer.Sent);
    }

    [Fact]
    public async Task ResetPassword_WithTheEmailedToken_Works()
    {
        await RegisterAsync();
        await NewService().RequestPasswordResetAsync(new ForgotPasswordRequest { Email = Email }, "https://cal.example.com");

        var result = await NewService().ResetPasswordAsync(new ResetPasswordRequest
        {
            Email = Email,
            Token = TokenFromLink(),
            NewPassword = NewPassword,
        });

        Assert.True(result.Succeeded);
        Assert.True(await CanLogInAsync(NewPassword));
        Assert.False(await CanLogInAsync(OriginalPassword));
    }

    [Fact]
    public async Task ResetPassword_TokenCannotBeUsedTwice()
    {
        await RegisterAsync();
        await NewService().RequestPasswordResetAsync(new ForgotPasswordRequest { Email = Email }, "https://cal.example.com");
        var token = TokenFromLink();

        Assert.True((await NewService().ResetPasswordAsync(new ResetPasswordRequest
        {
            Email = Email, Token = token, NewPassword = NewPassword,
        })).Succeeded);

        // Replaying the same link must not set the password a second time — the token is bound to
        // the security stamp, which the first reset rolled.
        var replay = await NewService().ResetPasswordAsync(new ResetPasswordRequest
        {
            Email = Email, Token = token, NewPassword = "Third3#password",
        });

        Assert.False(replay.Succeeded);
        Assert.True(await CanLogInAsync(NewPassword));
    }

    [Fact]
    public async Task ResetPassword_WithAGarbageToken_FailsWithoutLeakingWhy()
    {
        await RegisterAsync();

        var result = await NewService().ResetPasswordAsync(new ResetPasswordRequest
        {
            Email = Email, Token = "not-a-real-token", NewPassword = NewPassword,
        });

        Assert.False(result.Succeeded);
        Assert.Contains("no longer valid", Assert.Single(result.Errors));
        Assert.True(await CanLogInAsync(OriginalPassword));
    }

    [Fact]
    public async Task ResetPassword_ForAnUnknownAddress_LooksTheSameAsABadToken()
    {
        await RegisterAsync();

        var result = await NewService().ResetPasswordAsync(new ResetPasswordRequest
        {
            Email = "nobody@test.com", Token = "whatever", NewPassword = NewPassword,
        });

        Assert.False(result.Succeeded);
        Assert.Contains("no longer valid", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task ResetPassword_ClearsALockout()
    {
        await RegisterAsync();
        for (var i = 0; i < 10; i++)
        {
            await CanLogInAsync("wrong-password");
        }
        Assert.False(await CanLogInAsync(OriginalPassword)); // locked out

        await NewService().RequestPasswordResetAsync(new ForgotPasswordRequest { Email = Email }, "https://cal.example.com");
        await NewService().ResetPasswordAsync(new ResetPasswordRequest
        {
            Email = Email, Token = TokenFromLink(), NewPassword = NewPassword,
        });

        // Proving control of the mailbox is a stronger claim than the guesses that caused the
        // lockout, so recovery has to actually let you back in.
        Assert.True(await CanLogInAsync(NewPassword));
    }

    // ---------------------------------------------------------------- registration switch

    [Fact]
    public async Task Registration_CanBeDisabled()
    {
        var service = NewService(disableRegistration: true);

        var result = await service.RegisterAsync(new RegisterRequest { Email = Email, Password = OriginalPassword });

        Assert.False(result.Succeeded);
        Assert.False(service.GetConfig().RegistrationEnabled);
        Assert.Null(await _users.FindByEmailAsync(Email));
    }

    [Fact]
    public async Task DisablingRegistration_DoesNotAffectExistingAccounts()
    {
        await RegisterAsync();

        var locked = NewService(disableRegistration: true);
        var login = await locked.LoginAsync(new LoginRequest { Email = Email, Password = OriginalPassword });

        Assert.True(login.Succeeded);
    }

    [Fact]
    public void Registration_IsOpenByDefault()
    {
        Assert.True(NewService().GetConfig().RegistrationEnabled);
    }
}
