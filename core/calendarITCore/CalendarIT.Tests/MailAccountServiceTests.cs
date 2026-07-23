using CalendarIT.Application.Mail;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

public sealed class MailAccountServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly MailAccountService _service;
    private readonly Guid _userId = Guid.NewGuid();

    public MailAccountServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "mail@test", NormalizedUserName = "MAIL@TEST" });
        _db.SaveChanges();
        _service = new MailAccountService(_db, new EphemeralDataProtectionProvider(), TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static SaveMailAccountRequest Request(string? password = "s3cret", string? fromAddress = null) => new()
    {
        Address = "richy@example.com",
        FromAddress = fromAddress,
        SmtpHost = "smtp.example.com",
        SmtpPort = 587,
        SmtpUseSsl = false,
        ImapHost = "imap.example.com",
        ImapPort = 993,
        ImapUseSsl = true,
        Username = "richy@example.com",
        Password = password,
    };

    [Fact]
    public async Task SaveAndGet_RoundTrips_WithoutExposingThePassword()
    {
        await _service.SaveAsync(_userId, Request());

        var dto = await _service.GetAsync(_userId);
        Assert.NotNull(dto);
        Assert.Equal("richy@example.com", dto!.Address);
        Assert.Equal("smtp.example.com", dto.SmtpHost);
        Assert.True(dto.HasPassword);

        // The password is stored, but only as ciphertext.
        var stored = await _db.MailAccounts.SingleAsync();
        Assert.NotEqual("s3cret", stored.PasswordProtected);
        Assert.DoesNotContain("s3cret", stored.PasswordProtected);
    }

    [Fact]
    public async Task Save_WithBlankPassword_KeepsTheStoredOne()
    {
        await _service.SaveAsync(_userId, Request(password: "s3cret"));
        var before = (await _db.MailAccounts.AsNoTracking().SingleAsync()).PasswordProtected;

        await _service.SaveAsync(_userId, Request(password: null));

        var after = (await _db.MailAccounts.AsNoTracking().SingleAsync()).PasswordProtected;
        Assert.Equal(before, after);
        Assert.True((await _service.GetAsync(_userId))!.HasPassword);
    }

    [Fact]
    public async Task Get_WhenProtectionKeysWereLost_DegradesToHasPasswordFalse()
    {
        await _service.SaveAsync(_userId, Request());

        // A different provider can't unprotect the old ciphertext — like lost/rotated keys.
        var rekeyed = new MailAccountService(_db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var dto = await rekeyed.GetAsync(_userId);

        Assert.NotNull(dto);
        Assert.False(dto!.HasPassword);
    }

    [Fact]
    public async Task Save_RoundTripsTheFromAddress_AndBlankBecomesNull()
    {
        await _service.SaveAsync(_userId, Request(fromAddress: "  noreply@example.com "));
        Assert.Equal("noreply@example.com", (await _service.GetAsync(_userId))!.FromAddress);

        await _service.SaveAsync(_userId, Request(fromAddress: "   "));
        Assert.Null((await _service.GetAsync(_userId))!.FromAddress);
    }

    [Fact]
    public async Task Delete_RemovesTheAccount()
    {
        await _service.SaveAsync(_userId, Request());
        Assert.True(await _service.DeleteAsync(_userId));
        Assert.Null(await _service.GetAsync(_userId));
        Assert.False(await _service.DeleteAsync(_userId));
    }
}
