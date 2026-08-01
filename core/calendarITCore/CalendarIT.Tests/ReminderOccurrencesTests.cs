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
}
