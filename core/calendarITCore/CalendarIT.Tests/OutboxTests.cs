using System.Net;
using CalendarIT.Application;
using CalendarIT.Application.Mail;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Net;
using CalendarIT.Infrastructure.Notifications;
using CalendarIT.Infrastructure.Persistence;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace CalendarIT.Tests;

/// <summary>
/// Mail is queued and sent in the background. These pin what that buys: nothing is lost when a
/// mail server is down, nothing is sent twice, one user's broken mailbox never delays anyone
/// else's mail, and mail that has gone stale is dropped rather than delivered late.
/// </summary>
public sealed class OutboxTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
    private readonly MailAccountService _accounts;
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();

    public OutboxTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        foreach (var (id, name) in new[] { (_alice, "alice@test"), (_bob, "bob@test") })
        {
            _db.Users.Add(new ApplicationUser
            {
                Id = id, UserName = name, NormalizedUserName = name.ToUpperInvariant(),
                Email = name, NormalizedEmail = name.ToUpperInvariant(),
            });
        }
        _db.SaveChanges();
        _accounts = new MailAccountService(_db, new EphemeralDataProtectionProvider(), _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NoSignal : IOutboxSignal
    {
        public int Pokes { get; private set; }

        public void Poke() => Pokes++;
    }

    /// <summary>A mail server that records what it was given — and can be told to misbehave.</summary>
    private sealed class FakeServer
    {
        public List<(string User, MimeMessage Message)> Delivered { get; } = [];
        public HashSet<string> Unreachable { get; } = [];
        public HashSet<string> RefusedRecipients { get; } = [];

        public Task<IMailSession> Connect(MailAccount account, string password, CancellationToken _) =>
            Unreachable.Contains(account.Username)
                ? throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)
                : Task.FromResult<IMailSession>(new Session(this, account.Username));

        private sealed class Session(FakeServer server, string user) : IMailSession
        {
            public Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
            {
                var to = message.To.Mailboxes.First().Address;
                if (server.RefusedRecipients.Contains(to))
                {
                    throw new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, "no such user");
                }
                server.Delivered.Add((user, message));
                return Task.CompletedTask;
            }

            public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public void Dispose()
            {
            }
        }
    }

    private async Task ConnectMailAsync(Guid userId, string address) =>
        await _accounts.SaveAsync(userId, new SaveMailAccountRequest
        {
            Address = address, SmtpHost = "smtp.example.com", SmtpPort = 587, Username = address, Password = "pw",
        });

    private MailOutbox Outbox(NoSignal? signal = null) => new(_db, _accounts, _clock, signal ?? new NoSignal());

    private static MimeMessage Message(string to, string subject = "Hello")
    {
        var m = new MimeMessage();
        m.To.Add(MailboxAddress.Parse(to));
        m.Subject = subject;
        m.Body = new TextPart("plain") { Text = "body" };
        return m;
    }

    private OutboxDispatchJob Job() =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _clock, NullLogger<OutboxDispatchJob>.Instance);

    private Task RunAsync(FakeServer server) =>
        Job().RunAsync(_db, _accounts, new MailConnectionOptions(), connect: server.Connect);

    [Fact]
    public async Task Queue_WithoutAMailAccount_QueuesNothing()
    {
        Assert.False(await Outbox().QueueAsync(_alice, Message("x@example.com"), OutboxKind.Invitation));
        Assert.Empty(_db.OutboxMessages);
    }

    [Fact]
    public async Task Queue_StoresTheMessage_FromTheAccount_AndWakesTheSender()
    {
        await ConnectMailAsync(_alice, "alice@example.com");
        var signal = new NoSignal();

        Assert.True(await Outbox(signal).QueueAsync(_alice, Message("x@example.com"), OutboxKind.Invitation));

        var row = Assert.Single(_db.OutboxMessages);
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal("x@example.com", row.Recipient);
        Assert.NotNull(row.Mime);
        Assert.Equal(1, signal.Pokes);
    }

    [Fact]
    public async Task Dispatch_SendsAndKeepsOnlyARecord()
    {
        await ConnectMailAsync(_alice, "alice@example.com");
        await Outbox().QueueAsync(_alice, Message("x@example.com"), OutboxKind.Invitation);
        var server = new FakeServer();

        await RunAsync(server);

        var (_, delivered) = Assert.Single(server.Delivered);
        Assert.Equal("alice@example.com", delivered.From.Mailboxes.Single().Address);
        var row = await _db.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal(OutboxStatus.Sent, row.Status);
        Assert.Null(row.Mime);

        await RunAsync(server); // a second pass sends nothing again
        Assert.Single(server.Delivered);
    }

    [Fact]
    public async Task Dispatch_OneUsersBrokenServer_DoesNotDelayAnotherUsersMail()
    {
        await ConnectMailAsync(_alice, "alice@example.com");
        await ConnectMailAsync(_bob, "bob@example.com");
        await Outbox().QueueAsync(_alice, Message("x@example.com"), OutboxKind.Invitation);
        await Outbox().QueueAsync(_bob, Message("y@example.com"), OutboxKind.Invitation);
        var server = new FakeServer();
        server.Unreachable.Add("alice@example.com");

        await RunAsync(server);

        Assert.Equal("bob@example.com", Assert.Single(server.Delivered).User);
        var alices = await _db.OutboxMessages.AsNoTracking().SingleAsync(m => m.UserId == _alice);
        Assert.Equal(OutboxStatus.Pending, alices.Status);
        Assert.Equal(1, alices.Attempts);
        Assert.Equal("The server couldn't be reached.", alices.LastError);
        Assert.Equal(_clock.Now.UtcDateTime + OutboxDispatchJob.Backoff(1), alices.NextAttemptAtUtc);
    }

    [Fact]
    public async Task Dispatch_RetriesAfterTheBackoff_AndSendsOnceTheServerIsBack()
    {
        await ConnectMailAsync(_alice, "alice@example.com");
        await Outbox().QueueAsync(_alice, Message("x@example.com"), OutboxKind.Invitation);
        var server = new FakeServer();
        server.Unreachable.Add("alice@example.com");
        await RunAsync(server);

        server.Unreachable.Clear();
        await RunAsync(server); // too early: still backing off
        Assert.Empty(server.Delivered);

        _clock.Now += OutboxDispatchJob.Backoff(1);
        await RunAsync(server);
        Assert.Single(server.Delivered);
    }

    [Fact]
    public async Task Dispatch_GivesUpAfterTheLastAttempt()
    {
        await ConnectMailAsync(_alice, "alice@example.com");
        await Outbox().QueueAsync(_alice, Message("x@example.com"), OutboxKind.Invitation);
        var server = new FakeServer();
        server.Unreachable.Add("alice@example.com");

        for (var i = 0; i < OutboxDispatchJob.MaxAttempts; i++)
        {
            await RunAsync(server);
            _clock.Now += TimeSpan.FromHours(6);
        }

        var row = await _db.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal(OutboxStatus.Failed, row.Status);
    }

    [Fact]
    public async Task Dispatch_ARefusedRecipient_FailsOnlyThatMessage()
    {
        await ConnectMailAsync(_alice, "alice@example.com");
        await Outbox().QueueAsync(_alice, Message("nobody@example.com", "first"), OutboxKind.Invitation);
        _clock.Now += TimeSpan.FromSeconds(1);
        await Outbox().QueueAsync(_alice, Message("y@example.com", "second"), OutboxKind.Invitation);
        var server = new FakeServer();
        server.RefusedRecipients.Add("nobody@example.com");

        await RunAsync(server);

        Assert.Equal("second", Assert.Single(server.Delivered).Message.Subject);
        var refused = await _db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Recipient == "nobody@example.com");
        Assert.Equal(OutboxStatus.Failed, refused.Status);
    }

    [Fact]
    public async Task Dispatch_DropsAReminderThatIsNoLongerWorthSending()
    {
        await ConnectMailAsync(_alice, "alice@example.com");
        await Outbox().QueueAsync(_alice, Message("alice@example.com"), OutboxKind.Reminder);
        _clock.Now += MailOutbox.LifetimeOf(OutboxKind.Reminder) + TimeSpan.FromMinutes(1);
        var server = new FakeServer();

        await RunAsync(server);

        Assert.Empty(server.Delivered);
        Assert.Equal(OutboxStatus.Failed, (await _db.OutboxMessages.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Dispatch_DeletesAPasswordResetOnceSent()
    {
        // The body carries a live reset token; it has no business staying in the database.
        await ConnectMailAsync(_alice, "alice@example.com");
        await Outbox().QueueAsync(_alice, Message("alice@example.com"), OutboxKind.PasswordReset);

        await RunAsync(new FakeServer());

        Assert.Empty(_db.OutboxMessages);
    }

    [Fact]
    public async Task RetryingAFailedMessage_QueuesItAgain()
    {
        await ConnectMailAsync(_alice, "alice@example.com");
        await Outbox().QueueAsync(_alice, Message("nobody@example.com"), OutboxKind.Invitation);
        var server = new FakeServer();
        server.RefusedRecipients.Add("nobody@example.com");
        await RunAsync(server);
        var id = (await _db.OutboxMessages.AsNoTracking().SingleAsync()).Id;

        var listed = Assert.Single(await _accounts.ListOutboxAsync(_alice));
        Assert.True(listed.CanRetry);
        Assert.True(await _accounts.RetryOutboxAsync(_alice, id));
        Assert.False(await _accounts.RetryOutboxAsync(_bob, id)); // not Bob's to touch

        server.RefusedRecipients.Clear();
        _db.ChangeTracker.Clear();
        await RunAsync(server);
        Assert.Single(server.Delivered);
    }

    // ---------------------------------------------------------------- the reminder job

    [Fact]
    public async Task ReminderJob_QueuesTheMailTogetherWithItsSentRecord()
    {
        await ConnectMailAsync(_alice, "alice@example.com");
        var calendar = await DefaultCalendar.GetOrCreateAsync(_db, _clock, _alice);
        AddReminderEvent(calendar.Id, "Dentist", rrule: null);
        await _db.SaveChangesAsync();

        await ReminderJob().RunAsync(_db, Outbox(), new FakeWebPushSender());

        Assert.Equal(OutboxKind.Reminder, (await _db.OutboxMessages.SingleAsync()).Kind);
        Assert.Equal(1, await _db.NotificationLogs.CountAsync());
    }

    [Fact]
    public async Task ReminderJob_AnotherUsersUnreadableSeries_DoesNotStopTheRun()
    {
        await ConnectMailAsync(_alice, "alice@example.com");
        var bobs = await DefaultCalendar.GetOrCreateAsync(_db, _clock, _bob);
        var alices = await DefaultCalendar.GetOrCreateAsync(_db, _clock, _alice);
        AddReminderEvent(bobs.Id, "Broken", rrule: "BOGUS"); // written before rules were validated
        AddReminderEvent(alices.Id, "Dentist", rrule: null);
        await _db.SaveChangesAsync();

        await ReminderJob().RunAsync(_db, Outbox(), new FakeWebPushSender());

        Assert.Contains("Dentist", (await _db.OutboxMessages.SingleAsync()).Subject);
    }

    private ReminderDispatchJob ReminderJob() =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _clock, NullLogger<ReminderDispatchJob>.Instance);

    private void AddReminderEvent(Guid calendarId, string title, string? rrule)
    {
        // Starts in an hour with a 60-minute reminder: due right now.
        var start = _clock.Now.UtcDateTime.AddHours(1);
        _db.Events.Add(new CalendarEvent
        {
            Id = Guid.NewGuid(), CalendarId = calendarId, Uid = $"{Guid.NewGuid():N}", Title = title,
            StartUtc = start, EndUtc = start.AddMinutes(30), RRule = rrule, TimeZoneId = "UTC", CreatedAt = start,
            Reminders = [new Reminder { Id = Guid.NewGuid(), MinutesBefore = 60, Channel = ReminderChannel.Email }],
        });
    }

    // ---------------------------------------------------------------- where mail may connect

    [Theory]
    [InlineData("127.0.0.1", OutboundHostScope.Private, false)]
    [InlineData("::1", OutboundHostScope.Private, false)]
    [InlineData("::ffff:127.0.0.1", OutboundHostScope.Private, false)]
    [InlineData("169.254.169.254", OutboundHostScope.Private, false)] // cloud metadata
    [InlineData("0.0.0.0", OutboundHostScope.Private, false)]
    [InlineData("10.0.0.5", OutboundHostScope.Private, true)]
    [InlineData("192.168.1.10", OutboundHostScope.Private, true)]
    [InlineData("10.0.0.5", OutboundHostScope.Public, false)]
    [InlineData("172.20.0.3", OutboundHostScope.Public, false)]
    [InlineData("fd00::1", OutboundHostScope.Public, false)]
    [InlineData("93.184.216.34", OutboundHostScope.Public, true)]
    [InlineData("127.0.0.1", OutboundHostScope.Any, true)]
    public void HostPolicy(string address, OutboundHostScope scope, bool allowed) =>
        Assert.Equal(allowed, OutboundHostPolicy.IsAllowed(IPAddress.Parse(address), scope));

    [Fact]
    public async Task SavingAMailAccount_PointingAtThisServer_IsRefused()
    {
        var request = new SaveMailAccountRequest
        {
            Address = "alice@example.com", SmtpHost = "127.0.0.1", SmtpPort = 5432, Username = "a", Password = "pw",
        };
        await Assert.ThrowsAsync<InvalidInputException>(() => _accounts.SaveAsync(_alice, request));
    }

    [Fact]
    public async Task TestingAMailAccount_ThatResolvesToLoopback_SaysSoWithoutDetails()
    {
        await _accounts.SaveAsync(_alice, new SaveMailAccountRequest
        {
            Address = "alice@example.com", SmtpHost = "localhost", SmtpPort = 25, Username = "a", Password = "pw",
        });

        var result = await _accounts.TestAsync(_alice);

        Assert.False(result.Ok);
        Assert.Equal("SMTP: That server address isn't allowed on this instance.", result.Error);
    }
}
