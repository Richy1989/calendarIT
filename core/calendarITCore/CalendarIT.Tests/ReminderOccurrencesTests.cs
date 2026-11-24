using CalendarIT.Domain;
using CalendarIT.Infrastructure.Notifications;

namespace CalendarIT.Tests;

public sealed class ReminderOccurrencesTests
{
    private static CalendarEvent Event(DateTime startUtc, string? rrule = null) => new()
    {
        Id = Guid.NewGuid(),
        Title = "Dentist",
        StartUtc = startUtc,
        EndUtc = startUtc.AddMinutes(30),
        RRule = rrule,
        TimeZoneId = "UTC",
    };

    [Fact]
    public void OneOff_InsideWindow_IsReturned()
    {
        var ev = Event(new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc));
        var hits = ReminderOccurrences.InWindow(
            ev, new DateTime(2026, 9, 1, 8, 59, 0, DateTimeKind.Utc), new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc), Assert.Single(hits));
    }

    [Fact]
    public void OneOff_OutsideWindow_IsNotReturned()
    {
        var ev = Event(new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc));
        var hits = ReminderOccurrences.InWindow(
            ev, new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 1, 9, 1, 0, DateTimeKind.Utc));
        Assert.Empty(hits); // start == occFrom is excluded (window is exclusive-left)
    }

    [Fact]
    public void Recurring_ReturnsTheOccurrenceInWindow_NotTheSeriesStart()
    {
        var ev = Event(new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc), rrule: "FREQ=DAILY");
        var hits = ReminderOccurrences.InWindow(
            ev, new DateTime(2026, 9, 1, 8, 59, 0, DateTimeKind.Utc), new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc), Assert.Single(hits));
    }

    [Theory]
    [InlineData("Europe/Vienna", 31, 22)]    // the 1st begins at 22:00Z the evening before
    [InlineData("America/New_York", 1, 4)]
    [InlineData(null, 1, 0)]                 // no zone (synced from a phone): midnight UTC
    public void AllDay_BeginsAtMidnightInItsOwnZone(string? zone, int day, int hour)
    {
        var date = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var begins = new DateTime(2026, day == 31 ? 8 : 9, day, hour, 0, 0, DateTimeKind.Utc);
        CalendarEvent[] events =
        [
            new() { Id = Guid.NewGuid(), Title = "Birthday", StartUtc = date, EndUtc = date, IsAllDay = true, TimeZoneId = zone },
            new()
            {
                Id = Guid.NewGuid(), Title = "Daily", StartUtc = date.AddDays(-7), EndUtc = date.AddDays(-7),
                IsAllDay = true, TimeZoneId = zone, RRule = "FREQ=DAILY",
            },
        ];

        foreach (var ev in events)
        {
            // What comes back is the stored date — the key the reminder is logged under.
            Assert.Equal(date, Assert.Single(ReminderOccurrences.InWindow(ev, begins.AddMinutes(-1), begins)));
            Assert.Empty(ReminderOccurrences.InWindow(ev, begins, begins.AddMinutes(1)));
        }
    }
}
