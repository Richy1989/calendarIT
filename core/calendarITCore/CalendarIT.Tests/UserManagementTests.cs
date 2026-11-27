using System.Web;
using CalendarIT.Application.Auth;
using CalendarIT.Application.Calendars;
using CalendarIT.Application.Mail;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Auth;
using CalendarIT.Infrastructure.Calendars;
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

namespace CalendarIT.Tests;

/// <summary>
/// The admin's user page: list, disable/enable, delete, admin rights and reset links — over a real
/// Identity stack and real sessions, so "disabled" is proven to stop every way in.
/// </summary>
public sealed class UserManagementTests : IDisposable
{
    private const string Password = "Password1#long";
    private const string AdminEmail = "admin@test.com";
    private const string UserEmail = "user@test.com";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly List<IServiceScope> _scopes = [];
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly CredentialEpochs _epochs = new();

    public UserManagementTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        // As in production (EF turns it on when it opens a connection itself): deletes cascade.
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }
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
        _cache.Dispose();
        _connection.Dispose();
    }

    private IServiceProvider Scope()
    {
        var scope = _services.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider;
    }

    private AppDbContext Db() => Scope().GetRequiredService<AppDbContext>();

    private AuthService Auth()
    {
        var scope = Scope();
        return new AuthService(
            scope.GetRequiredService<UserManager<ApplicationUser>>(), scope.GetRequiredService<AppDbContext>(),
            new TokenService(Options.Create(new JwtOptions
            {
                SigningKey = "test-signing-key-that-is-long-enough-32+", Issuer = "calendarit", Audience = "calendarit",
            }), TimeProvider.System),
            TimeProvider.System, new FakePasswordResetMailer(), Options.Create(new AuthOptions()),
            NullLogger<AuthService>.Instance, _cache, _epochs);
    }

    private AccountAdmin Admin()
    {
        var scope = Scope();
        return new AccountAdmin(scope.GetRequiredService<AppDbContext>(),
            scope.GetRequiredService<UserManager<ApplicationUser>>(), NullLogger<AccountAdmin>.Instance,
            Auth(), _epochs, Options.Create(new AuthOptions { PublicBaseUrl = "https://cal.example.com" }));
    }

    /// <summary>The server's admin (first account) and one active user.</summary>
    private async Task<(Guid Admin, Guid User)> TwoAccountsAsync()
    {
        Assert.NotNull((await Auth().RegisterAsync(new RegisterRequest { Email = AdminEmail, Password = Password })).Tokens);
        await Auth().RegisterAsync(new RegisterRequest { Email = UserEmail, Password = Password });
        var adminId = await IdOf(AdminEmail);
        var userId = await IdOf(UserEmail);
        await Admin().ActivateAsync(adminId, userId);
        return (adminId, userId);
    }

    private async Task<Guid> IdOf(string email) =>
        (await Scope().GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;

    private Task<AuthResult> LogInAsync(string email = UserEmail) =>
        Auth().LoginAsync(new LoginRequest { Email = email, Password = Password });

    [Fact]
    public async Task TheList_ShowsEveryAccount_WithItsState()
    {
        var (adminId, userId) = await TwoAccountsAsync();
        await Auth().RegisterAsync(new RegisterRequest { Email = "waiting@test.com", Password = Password });

        var list = (await Admin().ListUsersAsync(adminId))!;

        Assert.Equal(["waiting@test.com", AdminEmail, UserEmail], list.Select(u => u.Email)); // waiting first
        var admin = list.Single(u => u.Id == adminId);
        Assert.True(admin is { IsAdmin: true, IsYou: true, Active: true, Disabled: false });
        Assert.NotNull(admin.LastActiveAt); // signed up = signed in
        Assert.False(list.Single(u => u.Id == userId).IsAdmin);
        Assert.False(list.Single(u => u.Email == "waiting@test.com").Active);
    }

    [Fact]
    public async Task Disabling_StopsEveryWayIn_AndEnablingRestoresIt()
    {
        var (adminId, userId) = await TwoAccountsAsync();
        var session = (await LogInAsync()).Tokens!;

        Assert.Equal(AccountAdminResult.Done, (await Admin().SetDisabledAsync(adminId, userId, true)).Result);

        // The open session's refresh is refused (it was revoked), and so is a new sign-in.
        Assert.False((await Auth().RefreshAsync(new RefreshTokenRequest { RefreshToken = session.RefreshToken })).Succeeded);
        var login = await LogInAsync();
        Assert.Equal(AuthService.DisabledMessage, Assert.Single(login.Errors));
        // ...but only for someone with the right password.
        Assert.Equal("Invalid email or password.",
            Assert.Single((await Auth().LoginAsync(new LoginRequest { Email = UserEmail, Password = "Wrong1#password" })).Errors));
        Assert.Equal(1, _epochs.Of(userId)); // cached CalDAV logins retired
        Assert.True((await Admin().ListUsersAsync(adminId))!.Single(u => u.Id == userId).Disabled);

        Assert.Equal(AccountAdminResult.Done, (await Admin().SetDisabledAsync(adminId, userId, false)).Result);
        Assert.True((await LogInAsync()).Succeeded);
    }

    [Fact]
    public async Task TheAdmin_CantDisableDeleteOrDemoteThemselves()
    {
        var (adminId, _) = await TwoAccountsAsync();

        Assert.Equal(AccountAdminResult.Refused, (await Admin().SetDisabledAsync(adminId, adminId, true)).Result);
        Assert.Equal(AccountAdminResult.Refused, (await Admin().DeleteAsync(adminId, adminId)).Result);
        Assert.Equal(AccountAdminResult.Refused, (await Admin().SetAdminAsync(adminId, adminId, false)).Result);
        Assert.True(await Admin().IsAdminAsync(adminId)); // so a server always keeps one
    }

    [Fact]
    public async Task AdminRights_CanBeGiven_AndTakenAway()
    {
        var (adminId, userId) = await TwoAccountsAsync();

        Assert.Equal(AccountAdminResult.Done, (await Admin().SetAdminAsync(adminId, userId, true)).Result);
        Assert.NotNull(await Admin().ListUsersAsync(userId));
        // The new admin can now take the first one's rights away — and not the reverse at once.
        Assert.Equal(AccountAdminResult.Done, (await Admin().SetAdminAsync(userId, adminId, false)).Result);
        Assert.False(await Admin().IsAdminAsync(adminId));
        Assert.Equal(AccountAdminResult.Forbidden, (await Admin().SetAdminAsync(adminId, adminId, true)).Result);
    }

    [Fact]
    public async Task OnlyAnActiveEnabledAccount_CanBeMadeAdmin_AndADisabledAdminCantAct()
    {
        var (adminId, userId) = await TwoAccountsAsync();
        await Auth().RegisterAsync(new RegisterRequest { Email = "waiting@test.com", Password = Password });

        Assert.Equal(AccountAdminResult.Refused, (await Admin().SetAdminAsync(adminId, await IdOf("waiting@test.com"), true)).Result);

        await Admin().SetAdminAsync(adminId, userId, true);
        await Admin().SetDisabledAsync(adminId, userId, true);
        Assert.False(await Admin().IsAdminAsync(userId));
        Assert.Null(await Admin().ListUsersAsync(userId));
    }

    [Fact]
    public async Task Deleting_RemovesTheAccountAndEverythingInIt()
    {
        var (adminId, userId) = await TwoAccountsAsync();
        await LogInAsync(); // a session
        var db = Db();
        var calendar = await new CalendarService(db, TimeProvider.System).CreateAsync(userId, new SaveCalendarRequest { Name = "Work" });
        await new EventService(db, TimeProvider.System, new FakeInvitationMailer(), new InternalInvitationDelivery(db, TimeProvider.System))
            .CreateAsync(userId, new SaveEventRequest
            {
                Title = "Standup", CalendarId = calendar.Id,
                Start = new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero),
                End = new DateTimeOffset(2026, 12, 1, 9, 15, 0, TimeSpan.Zero),
                Reminders = [new ReminderInput { MinutesBefore = 10, Channel = "Email" }],
            });
        await new TaskService(db, TimeProvider.System).CreateAsync(userId, new SaveTaskRequest { Title = "Write report" });
        await new CategoryService(db, TimeProvider.System).CreateAsync(userId, new SaveCategoryRequest { Name = "Mine", Color = "#112233" });
        var accounts = new MailAccountService(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        await accounts.SaveAsync(userId, new SaveMailAccountRequest
        {
            Address = UserEmail, SmtpHost = "smtp.example.com", SmtpPort = 587, Username = UserEmail, Password = "pw",
        });

        Assert.Equal(AccountAdminResult.Done, (await Admin().DeleteAsync(adminId, userId)).Result);

        var after = Db();
        Assert.False(await after.Users.AnyAsync(u => u.Id == userId));
        Assert.False(await after.Calendars.AnyAsync(c => c.OwnerUserId == userId));
        Assert.Empty(await after.Events.ToListAsync());
        Assert.Empty(await after.Tasks.ToListAsync());
        Assert.False(await after.Categories.AnyAsync(c => c.OwnerUserId == userId));
        Assert.False(await after.MailAccounts.AnyAsync(a => a.UserId == userId));
        Assert.False(await after.RefreshTokens.AnyAsync(t => t.UserId == userId));
        Assert.True((await Admin().ListUsersAsync(adminId))!.Count == 1);
    }

    [Fact]
    public async Task AResetLink_LetsTheUserChooseANewPassword()
    {
        var (adminId, userId) = await TwoAccountsAsync();

        var outcome = await Admin().CreateResetLinkAsync(adminId, userId);

        Assert.StartsWith("https://cal.example.com/reset-password?email=", outcome.Link);
        var token = HttpUtility.ParseQueryString(new Uri(outcome.Link!).Query)["token"]!;
        Assert.True((await Auth().ResetPasswordAsync(new ResetPasswordRequest
        {
            Email = UserEmail, Token = token, NewPassword = "Brand2#newpass",
        })).Succeeded);
        Assert.True((await Auth().LoginAsync(new LoginRequest { Email = UserEmail, Password = "Brand2#newpass" })).Succeeded);
    }
}
