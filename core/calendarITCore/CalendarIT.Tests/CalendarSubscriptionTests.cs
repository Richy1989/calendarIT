using System.Net;
using System.Text;
using CalendarIT.Application;
using CalendarIT.Application.Calendars;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarIT.Tests;

/// <summary>
/// Calendar subscriptions: the feed fetch (a URL is outside input aimed at the outside world)
/// and the mirror it feeds — plus the read-only rule everywhere a subscribed calendar could be
/// written to.
/// </summary>
public sealed class CalendarSubscriptionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly FakeFeed _feed = new();

    public CalendarSubscriptionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "sub@test", NormalizedUserName = "SUB@TEST" });
        _db.SaveChanges();
        DefaultCalendar.GetOrCreateAsync(_db, TimeProvider.System, _userId).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>A feed whose next answer the test sets.</summary>
    private sealed class FakeFeed : CalendarFeedFetcher
    {
        public FeedFetchResult Next { get; set; } = new FeedFetchResult.Failed("unset");

        public List<(string Url, string? ETag)> Calls { get; } = [];

        public FakeFeed() : base(new HttpClientHandler()) { }

        public override Task<FeedFetchResult> FetchAsync(string url, string? etag, string? lastModified, CancellationToken cancellationToken)
        {
            Calls.Add((url, etag));
            return Task.FromResult(Next);
        }
    }

    private CalendarSubscriptionService Subscriptions() =>
        new(_db, TimeProvider.System, _feed, NullLogger<CalendarSubscriptionService>.Instance);

    private static string Ics(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nX-WR-CALNAME:Holidays\r\n" + string.Concat(events) + "END:VCALENDAR\r\n";

    private static string Event(string uid, string summary, string date) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260101T000000Z\r\nSUMMARY:{summary}\r\nDTSTART;VALUE=DATE:{date}\r\nEND:VEVENT\r\n";

    private static FeedFetchResult.Fetched Fetched(string ics, string? etag = null) => new(ics, etag, null);

    private async Task<CalendarDto> SubscribeAsync(string ics)
    {
        _feed.Next = Fetched(ics, "\"v1\"");
        return await Subscriptions().SubscribeAsync(_userId, new SubscribeCalendarRequest { Url = "webcal://example.com/holidays.ics" });
    }

    [Fact]
    public async Task Subscribing_CreatesAReadOnlyCalendar_WithTheFeedsEvents_AndName()
    {
        var calendar = await SubscribeAsync(Ics(Event("a@x", "New Year", "20270101"), Event("b@x", "Easter", "20270328")));

        Assert.Equal("Holidays", calendar.Name);
        Assert.Equal(2, calendar.EventCount);
        Assert.Equal("https://example.com/holidays.ics", calendar.Subscription!.Url);
        Assert.Equal("https://example.com/holidays.ics", _feed.Calls.Single().Url); // webcal → https
        Assert.Null(calendar.Subscription.LastError);
    }

    [Fact]
    public async Task ABadFeed_LeavesNoCalendarBehind()
    {
        _feed.Next = new FeedFetchResult.Failed("example.com answered 404 Not Found.");

        var error = await Assert.ThrowsAsync<InvalidInputException>(() =>
            Subscriptions().SubscribeAsync(_userId, new SubscribeCalendarRequest { Url = "https://example.com/x.ics" }));

        Assert.Contains("404", error.Message);
        Assert.Single(_db.Calendars);
    }

    [Theory]
    [InlineData("ftp://example.com/x.ics")]
    [InlineData("https://127.0.0.1/x.ics")]
    [InlineData("https://192.168.1.10/x.ics")]
    [InlineData("https://[::1]/x.ics")]
    [InlineData("not a url")]
    public async Task AddressesThatArentPublicWebAddresses_AreRefused_BeforeAnyFetch(string url)
    {
        await Assert.ThrowsAsync<InvalidInputException>(() =>
            Subscriptions().SubscribeAsync(_userId, new SubscribeCalendarRequest { Url = url }));
        Assert.Empty(_feed.Calls);
    }

    [Fact]
    public async Task ARefresh_AddsUpdatesAndRemoves_ByUid_AndKeepsUnchangedEventsUntouched()
    {
        var calendar = await SubscribeAsync(Ics(Event("a@x", "New Year", "20270101"), Event("b@x", "Easter", "20270328")));
        var stamp = _db.Events.AsNoTracking().Single(e => e.Uid == "a@x").UpdatedAt;
        await Task.Delay(20);

        _feed.Next = Fetched(Ics(Event("a@x", "New Year", "20270101"), Event("c@x", "May Day", "20270501"), Event("b@x", "Easter Sunday", "20270328")));
        var refreshed = await Subscriptions().RefreshAsync(_userId, calendar.Id);

        Assert.Equal(3, refreshed!.EventCount);
        var events = _db.Events.AsNoTracking().Where(e => e.CalendarId == calendar.Id).ToList();
        Assert.Equal(["Easter Sunday", "May Day", "New Year"], events.Select(e => e.Title).Order());
        Assert.Equal(stamp, events.Single(e => e.Uid == "a@x").UpdatedAt); // phones needn't fetch it again

        _feed.Next = Fetched(Ics(Event("c@x", "May Day", "20270501")));
        await Subscriptions().RefreshAsync(_userId, calendar.Id);

        Assert.Equal(["May Day"], _db.Events.AsNoTracking().Where(e => e.CalendarId == calendar.Id).Select(e => e.Title));
    }

    [Fact]
    public async Task AFailedRefresh_KeepsTheEvents_AndSaysWhy()
    {
        var calendar = await SubscribeAsync(Ics(Event("a@x", "New Year", "20270101")));

        _feed.Next = new FeedFetchResult.Failed("Couldn't connect to example.com.");
        var refreshed = await Subscriptions().RefreshAsync(_userId, calendar.Id);

        Assert.Equal("Couldn't connect to example.com.", refreshed!.Subscription!.LastError);
        Assert.Equal(1, refreshed.EventCount);
    }

    [Fact]
    public async Task TheJob_SendsTheETag_AndANotModified_ChangesNothing()
    {
        var calendar = await SubscribeAsync(Ics(Event("a@x", "New Year", "20270101")));
        var row = _db.Calendars.Single(c => c.Id == calendar.Id);
        row.NextFetchUtc = DateTime.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        _feed.Next = new FeedFetchResult.NotModified();
        Assert.Equal(1, await Subscriptions().RefreshDueAsync());

        Assert.Equal("\"v1\"", _feed.Calls.Last().ETag);
        Assert.Equal(1, _db.Events.Count(e => e.CalendarId == calendar.Id));
        Assert.Equal(0, await Subscriptions().RefreshDueAsync()); // next one is hours away
    }

    [Fact]
    public async Task ASubscription_CantBeWrittenTo_FromTheApp()
    {
        var calendar = await SubscribeAsync(Ics(Event("a@x", "New Year", "20270101")));
        var events = new EventService(_db, TimeProvider.System, new FakeInvitationMailer(), new InternalInvitationDelivery(_db, TimeProvider.System));
        var existing = _db.Events.AsNoTracking().Single(e => e.CalendarId == calendar.Id);
        var request = new SaveEventRequest { Title = "Mine", Start = DateTimeOffset.UtcNow, CalendarId = calendar.Id };

        await Assert.ThrowsAsync<InvalidInputException>(() => events.CreateAsync(_userId, request));
        await Assert.ThrowsAsync<InvalidInputException>(() => events.UpdateAsync(_userId, existing.Id, request));
        await Assert.ThrowsAsync<InvalidInputException>(() => events.DeleteAsync(_userId, existing.Id, null));
        var io = new CalendarIoService(_db, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidInputException>(() => io.ImportAsync(_userId, Ics(Event("z@x", "Sneaky", "20270202")), calendar.Id));
    }

    [Fact]
    public async Task NewEvents_NeverLandInASubscription_AndTheLastOwnCalendarStays()
    {
        var subscription = await SubscribeAsync(Ics(Event("a@x", "New Year", "20270101")));
        var own = _db.Calendars.Single(c => c.SourceUrl == null);

        Assert.Equal(own.Id, (await DefaultCalendar.GetOrCreateAsync(_db, TimeProvider.System, _userId)).Id);
        var calendars = new CalendarService(_db, TimeProvider.System);
        Assert.Equal(DeleteCalendarResult.LastCalendar, await calendars.DeleteAsync(_userId, own.Id));
        Assert.Equal(DeleteCalendarResult.Deleted, await calendars.DeleteAsync(_userId, subscription.Id));
    }

    [Fact]
    public async Task APasswordInTheUrl_IsNeverShown()
    {
        _feed.Next = Fetched(Ics());
        var calendar = await Subscriptions().SubscribeAsync(_userId, new SubscribeCalendarRequest { Url = "https://me:secret@example.com/cal.ics" });

        Assert.DoesNotContain("secret", calendar.Subscription!.Url);
        Assert.Contains("me:", calendar.Subscription.Url);
    }
}

/// <summary>The fetcher itself, against a stand-in network.</summary>
public sealed class CalendarFeedFetcherTests
{
    private const string Ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n";

    /// <summary>Answers by URL, and records what each request carried.</summary>
    private sealed class Network(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/calendar") };

    private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };

    [Fact]
    public async Task ALogin_FollowsARedirectOnTheSameHost_ButNeverToAnother()
    {
        var net = new Network(r => r.RequestUri!.AbsolutePath switch
        {
            "/a" => Redirect("https://cal.example.com/b"),
            "/b" => Redirect("https://elsewhere.example.net/c"),
            _ => Ok(Ics),
        });

        var result = await new CalendarFeedFetcher(net).FetchAsync("https://me:pw@cal.example.com/a", null, null, default);

        Assert.IsType<FeedFetchResult.Fetched>(result);
        Assert.NotNull(net.Requests[0].Headers.Authorization);
        Assert.NotNull(net.Requests[1].Headers.Authorization);
        Assert.Null(net.Requests[2].Headers.Authorization);
        Assert.All(net.Requests, r => Assert.True(string.IsNullOrEmpty(r.RequestUri!.UserInfo)));
    }

    [Fact]
    public async Task EndlessRedirects_Stop()
    {
        var net = new Network(r => Redirect("https://cal.example.com/again"));

        var result = await new CalendarFeedFetcher(net).FetchAsync("https://cal.example.com/a", null, null, default);

        Assert.Contains("too many", Assert.IsType<FeedFetchResult.Failed>(result).Message);
        Assert.Equal(6, net.Requests.Count);
    }

    [Fact]
    public async Task ARedirectToAnotherScheme_IsRefused()
    {
        var net = new Network(r => Redirect("file:///etc/passwd"));

        Assert.IsType<FeedFetchResult.Failed>(await new CalendarFeedFetcher(net).FetchAsync("https://cal.example.com/a", null, null, default));
    }

    [Fact]
    public async Task SomethingThatIsntACalendar_IsRefused()
    {
        var net = new Network(r => Ok("<html>Sign in</html>"));

        Assert.Contains("calendar", Assert.IsType<FeedFetchResult.Failed>(
            await new CalendarFeedFetcher(net).FetchAsync("https://cal.example.com/a", null, null, default)).Message);
    }

    [Fact]
    public async Task AnOversizedFeed_IsRefused_EvenWithoutALength()
    {
        var huge = "BEGIN:VCALENDAR\r\n" + new string('x', CalendarFeedFetcher.MaxBytes);
        var net = new Network(r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(huge))) });

        Assert.Contains("larger", Assert.IsType<FeedFetchResult.Failed>(
            await new CalendarFeedFetcher(net).FetchAsync("https://cal.example.com/a", null, null, default)).Message);
    }

    [Fact]
    public async Task TheValidators_AreSent_AndNotModified_IsUnderstood()
    {
        var net = new Network(r => new HttpResponseMessage(HttpStatusCode.NotModified));

        var result = await new CalendarFeedFetcher(net).FetchAsync("https://cal.example.com/a", "\"v1\"", "Tue, 01 Sep 2026 08:00:00 GMT", default);

        Assert.IsType<FeedFetchResult.NotModified>(result);
        Assert.Equal("\"v1\"", net.Requests[0].Headers.IfNoneMatch.Single().ToString());
        Assert.NotNull(net.Requests[0].Headers.IfModifiedSince);
    }

    [Fact]
    public async Task TheRealNetwork_RefusesAnAddressInsideTheServersOwnNetwork()
    {
        // A hostname that resolves to loopback: caught on the address actually dialled.
        var result = await new CalendarFeedFetcher().FetchAsync("http://localhost:9/x.ics", null, null, default);

        Assert.Contains("public internet", Assert.IsType<FeedFetchResult.Failed>(result).Message);
    }
}
