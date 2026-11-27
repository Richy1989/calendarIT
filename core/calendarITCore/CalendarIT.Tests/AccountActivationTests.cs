using System.Text.RegularExpressions;
using System.Web;
using CalendarIT.Application.Auth;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Auth;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace CalendarIT.Tests;

/// <summary>
/// Account activation over a real Identity stack. Anyone can sign up with any address, so an
/// account signs in only once that address is proven — by the link mailed through the server's
/// own account, or by the admin. The first account on a server activates itself and is the admin.
/// </summary>
public sealed class AccountActivationTests : IDisposable
{
    private const string AdminEmail = "admin@test.com";
    private const string Email = "new@test.com";
    private const string Password = "Password1#long";
    private const string BaseUrl = "https://cal.example.com";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly FakeMailOutbox _outbox = new() { HasSystemMail = true };
    private readonly List<IServiceScope> _scopes = [];

    public AccountActivationTests()
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
        _services.GetRequiredService<AppDbContext>().Database.EnsureCreated();
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

    /// <summary>A fresh scope per call, as each request gets.</summary>
    private IServiceProvider Scope()
    {
        var scope = _services.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider;
    }

    private UserManager<ApplicationUser> Users() => Scope().GetRequiredService<UserManager<ApplicationUser>>();

    private EmailConfirmations Confirmations(bool systemMail = true, IMemoryCache? cache = null) => new(
        Users(), _outbox,
        systemMail ? new SystemMailOptions { Host = "relay.example.com", From = "noreply@example.com" } : new SystemMailOptions(),
        Options.Create(new AuthOptions { PublicBaseUrl = BaseUrl }),
        NullLogger<EmailConfirmations>.Instance, cache);

    private AuthService Auth(bool systemMail = true, IAdminSignupNotifier? notifier = null)
    {
        var scope = Scope();
        return new AuthService(
            scope.GetRequiredService<UserManager<ApplicationUser>>(), scope.GetRequiredService<AppDbContext>(),
            new TokenService(Options.Create(new JwtOptions
            {
                SigningKey = "test-signing-key-that-is-long-enough-32+", Issuer = "calendarit", Audience = "calendarit",
            }), TimeProvider.System),
            TimeProvider.System, new FakePasswordResetMailer(), Options.Create(new AuthOptions()),
            NullLogger<AuthService>.Instance, confirmations: Confirmations(systemMail), adminNotifier: notifier);
    }

    private AdminSignupNotifier Notifier(IMemoryCache? cache = null) => new(
        Scope().GetRequiredService<AppDbContext>(), _outbox, Options.Create(new AuthOptions { PublicBaseUrl = BaseUrl }),
        NullLogger<AdminSignupNotifier>.Instance, cache);

    private List<(Guid UserId, MimeMessage Message)> Notices(bool viaSystem) => viaSystem
        ? _outbox.SentViaSystem.Where(m => m.Kind == OutboxKind.AdminNotice).Select(m => (m.UserId, m.Message)).ToList()
        : _outbox.Sent.Where((_, i) => _outbox.Kinds[i] == OutboxKind.AdminNotice).ToList();

    private AccountAdmin Admin()
    {
        var scope = Scope();
        return new AccountAdmin(scope.GetRequiredService<AppDbContext>(),
            scope.GetRequiredService<UserManager<ApplicationUser>>(), NullLogger<AccountAdmin>.Instance);
    }

    private async Task<Guid> IdOf(string email) => (await Users().FindByEmailAsync(email))!.Id;

    /// <summary>The server's first account: active, and the admin.</summary>
    private async Task<Guid> FirstAccountAsync()
    {
        var first = await Auth().RegisterAsync(new RegisterRequest { Email = AdminEmail, Password = Password });
        Assert.NotNull(first.Tokens);
        return await IdOf(AdminEmail);
    }

    private Task<AuthResult> SignUpAsync(bool systemMail = true) =>
        Auth(systemMail).RegisterAsync(new RegisterRequest { Email = Email, Password = Password });

    private Task<AuthResult> LogInAsync(string email = Email, string password = Password) =>
        Auth().LoginAsync(new LoginRequest { Email = email, Password = password });

    private string LinkInMail()
    {
        var (_, message, kind) = Assert.Single(_outbox.SentViaSystem);
        Assert.Equal(OutboxKind.EmailConfirmation, kind);
        Assert.Equal(Email, message.To.Mailboxes.Single().Address);
        return Regex.Match(message.TextBody ?? string.Empty, @"https://\S+").Value;
    }

    private static string TokenOf(string link) => HttpUtility.ParseQueryString(new Uri(link).Query)["token"]!;

    [Fact]
    public async Task TheFirstAccount_IsActiveAtOnce_AndTheAdmin()
    {
        var adminId = await FirstAccountAsync();

        Assert.True(await Admin().IsAdminAsync(adminId));
        Assert.True((await LogInAsync(AdminEmail)).Succeeded);
        Assert.Empty(_outbox.SentViaSystem);
    }

    [Fact]
    public async Task ALaterSignUp_GetsNoTokens_AndCantSignIn_UntilActivated()
    {
        await FirstAccountAsync();

        var signUp = await SignUpAsync();

        Assert.True(signUp.Succeeded);
        Assert.Null(signUp.Tokens);
        Assert.Equal(AccountActivation.Email, signUp.Activation);
        var login = await LogInAsync();
        Assert.False(login.Succeeded);
        Assert.Equal(AuthService.NotActivatedMessage, Assert.Single(login.Errors));
        Assert.False(await Admin().IsAdminAsync(await IdOf(Email)));
    }

    [Fact]
    public async Task AWrongPassword_DoesNotRevealThatTheAccountWaitsForActivation()
    {
        await FirstAccountAsync();
        await SignUpAsync();

        Assert.Equal("Invalid email or password.", Assert.Single((await LogInAsync(password: "Wrong1#password")).Errors));
    }

    [Fact]
    public async Task TheMailedLink_ActivatesTheAccount()
    {
        await FirstAccountAsync();
        await SignUpAsync();
        var link = LinkInMail();
        Assert.StartsWith(BaseUrl + EmailConfirmations.Path + "?email=", link);
        Assert.Empty(_outbox.Sent); // never through a mail account the user chose

        Assert.True(await Confirmations().ConfirmAsync(new ConfirmEmailRequest { Email = Email, Token = TokenOf(link) }));

        Assert.True((await LogInAsync()).Succeeded);
    }

    [Fact]
    public async Task WithoutServerMail_TheAdminActivates()
    {
        var adminId = await FirstAccountAsync();

        var signUp = await SignUpAsync(systemMail: false);

        Assert.Equal(AccountActivation.Admin, signUp.Activation);
        Assert.Empty(_outbox.SentViaSystem);
        var waiting = (await Admin().ListUsersAsync(adminId))!.First();
        Assert.Equal(Email, waiting.Email);
        Assert.False(waiting.Active);
        Assert.NotNull(waiting.CreatedAt);

        Assert.Equal(AccountAdminResult.Done, (await Admin().ActivateAsync(adminId, waiting.Id)).Result);

        Assert.True((await LogInAsync()).Succeeded);
        Assert.All((await Admin().ListUsersAsync(adminId))!, u => Assert.True(u.Active));
    }

    [Fact]
    public async Task OnlyTheAdmin_ManagesAccounts()
    {
        var adminId = await FirstAccountAsync();
        await SignUpAsync(systemMail: false);
        var newId = await IdOf(Email);
        await Admin().ActivateAsync(adminId, newId);
        await Auth().RegisterAsync(new RegisterRequest { Email = "third@test.com", Password = Password });
        var thirdId = await IdOf("third@test.com");

        // An ordinary (active) account can't see or touch anyone.
        Assert.Null(await Admin().ListUsersAsync(newId));
        Assert.Equal(AccountAdminResult.Forbidden, (await Admin().ActivateAsync(newId, thirdId)).Result);
        Assert.Equal(AccountAdminResult.Forbidden, (await Admin().DeleteAsync(newId, thirdId)).Result);
        Assert.Equal(AccountAdminResult.Forbidden, (await Admin().SetDisabledAsync(newId, adminId, true)).Result);
        Assert.Equal(AccountAdminResult.Forbidden, (await Admin().SetAdminAsync(newId, newId, true)).Result);
        Assert.Equal(AccountAdminResult.Forbidden, (await Admin().CreateResetLinkAsync(newId, adminId)).Result);
        // ...and an account still waiting can't activate itself.
        Assert.Equal(AccountAdminResult.Forbidden, (await Admin().ActivateAsync(thirdId, thirdId)).Result);
    }

    [Fact]
    public async Task BadTokens_ActivateNothing()
    {
        await FirstAccountAsync();
        await SignUpAsync();
        var token = TokenOf(LinkInMail());

        Assert.False(await Confirmations().ConfirmAsync(new ConfirmEmailRequest { Email = Email, Token = "bm90LWEtdG9rZW4" }));
        Assert.False(await Confirmations().ConfirmAsync(new ConfirmEmailRequest { Email = Email, Token = "%%%" }));
        // Another account's link doesn't fit this one.
        Assert.False(await Confirmations().ConfirmAsync(new ConfirmEmailRequest { Email = AdminEmail + ".x", Token = token }));
        Assert.False((await LogInAsync()).Succeeded);
    }

    [Fact]
    public async Task Resending_ByAddress_SendsOnlyForAWaitingAccount_AndNotTooOften()
    {
        await FirstAccountAsync();
        await SignUpAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        _outbox.SentViaSystem.Clear();

        await Confirmations(cache: cache).SendToAddressAsync(Email);
        await Confirmations(cache: cache).SendToAddressAsync(Email); // moments later: held back
        await Confirmations(cache: cache).SendToAddressAsync(AdminEmail); // already active
        await Confirmations(cache: cache).SendToAddressAsync("nobody@test.com");

        Assert.Single(_outbox.SentViaSystem);
    }

    [Fact]
    public async Task AServerFromBeforeTheAdminRole_MakesItsOldestAccountTheAdmin()
    {
        var users = Users();
        foreach (var (email, created) in new[] { ("later@test.com", 2), ("oldest@test.com", 1) })
        {
            Assert.True((await users.CreateAsync(new ApplicationUser
            {
                UserName = email, Email = email, EmailConfirmed = true,
                CreatedAtUtc = new DateTime(2026, created, 1, 0, 0, 0, DateTimeKind.Utc),
            }, Password)).Succeeded);
        }

        await AdminRole.EnsureOneAsync(_services);
        await AdminRole.EnsureOneAsync(_services); // once there is one, nothing changes

        Assert.True(await Admin().IsAdminAsync(await IdOf("oldest@test.com")));
        Assert.False(await Admin().IsAdminAsync(await IdOf("later@test.com")));
    }

    [Fact]
    public async Task PasswordResets_PreferTheSystemAccount()
    {
        var mailer = new PasswordResetMailer(_outbox, NullLogger<PasswordResetMailer>.Instance);

        await mailer.SendAsync(Guid.NewGuid(), Email, BaseUrl + "/reset-password?token=x");

        Assert.Equal(OutboxKind.PasswordReset, Assert.Single(_outbox.SentViaSystem).Kind);
        Assert.Empty(_outbox.Sent);

        _outbox.HasSystemMail = false; // none configured: the user's own account, as before
        await mailer.SendAsync(Guid.NewGuid(), Email, BaseUrl + "/reset-password?token=y");
        Assert.Single(_outbox.Sent);
    }

    [Fact]
    public async Task ASignUp_TellsTheAdmin_ThroughTheServersMailAccount()
    {
        var adminId = await FirstAccountAsync();

        await Auth(notifier: Notifier()).RegisterAsync(new RegisterRequest { Email = Email, Password = Password });

        var (to, message) = Assert.Single(Notices(viaSystem: true));
        Assert.Equal(adminId, to);
        Assert.Equal(AdminEmail, message.To.Mailboxes.Single().Address);
        Assert.Contains(Email, message.TextBody);
        Assert.Contains(BaseUrl + "/?settings=users", message.TextBody);
    }

    [Fact]
    public async Task WithoutServerMail_TheNoticeGoesThroughTheAdminsOwnAccount()
    {
        var adminId = await FirstAccountAsync();
        _outbox.HasSystemMail = false;

        await Auth(systemMail: false, notifier: Notifier()).RegisterAsync(new RegisterRequest { Email = Email, Password = Password });

        var (from, message) = Assert.Single(Notices(viaSystem: false));
        Assert.Equal(adminId, from); // the admin's own account, mailing the admin
        Assert.Contains("can't sign in until you activate them", message.TextBody);
    }

    [Fact]
    public async Task ABurstOfSignUps_IsOneNotice_ListingEveryone()
    {
        await FirstAccountAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());

        for (var i = 0; i < 5; i++)
        {
            await Auth(notifier: Notifier(cache)).RegisterAsync(new RegisterRequest { Email = $"spam{i}@test.com", Password = Password });
        }

        var (_, message) = Assert.Single(Notices(viaSystem: true));
        Assert.Contains("spam0@test.com", message.TextBody); // the first sign-up's notice
        Assert.Equal(5, (await Admin().ListUsersAsync(await IdOf(AdminEmail)))!.Count(u => !u.Active)); // the app shows all
    }

    [Fact]
    public async Task TheFirstAccount_SendsNoNotice()
    {
        await Auth(notifier: Notifier()).RegisterAsync(new RegisterRequest { Email = AdminEmail, Password = Password });

        Assert.Empty(Notices(viaSystem: true));
    }
}
