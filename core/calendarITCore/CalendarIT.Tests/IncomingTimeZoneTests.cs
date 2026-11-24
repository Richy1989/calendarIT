using CalendarIT.Infrastructure.Calendars;
using Ical.Net;

namespace CalendarIT.Tests;

/// <summary>
/// Zone names as other calendars write them. Ical.Net reads a TZID its tz database doesn't know
/// as UTC without a word, so each of these used to land hours off (Outlook's own definitions, a
/// wrong case) or lose its zone and drift an hour at every DST change (a Mozilla path).
/// </summary>
public sealed class IncomingTimeZoneTests
{
    /// <summary>Central European time as Outlook writes it out, under any name.</summary>
    private const string CentralEurope = """
        BEGIN:VTIMEZONE
        TZID:{0}
        BEGIN:STANDARD
        DTSTART:16010101T030000
        TZOFFSETFROM:+0200
        TZOFFSETTO:+0100
        RRULE:FREQ=YEARLY;BYDAY=-1SU;BYMONTH=10
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:16010101T020000
        TZOFFSETFROM:+0100
        TZOFFSETTO:+0200
        RRULE:FREQ=YEARLY;BYDAY=-1SU;BYMONTH=3
        END:DAYLIGHT
        END:VTIMEZONE
        """;

    /// <summary>A zone no real one matches: +01:30 all year.</summary>
    private const string Invented = """
        BEGIN:VTIMEZONE
        TZID:{0}
        BEGIN:STANDARD
        DTSTART:19700101T000000
        TZOFFSETFROM:+0130
        TZOFFSETTO:+0130
        END:STANDARD
        END:VTIMEZONE
        """;

    private static CalendarIT.Domain.CalendarEvent Import(string tzid, string? definition, string extra = "")
    {
        var vtz = definition is null ? "" : definition.Replace("{0}", tzid) + "\n";
        var ics = $"""
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//test//EN
            {vtz}BEGIN:VEVENT
            UID:zone-test
            DTSTAMP:20260101T000000Z
            DTSTART;TZID="{tzid}":20260715T100000
            DTEND;TZID="{tzid}":20260715T110000
            RRULE:FREQ=WEEKLY
            {extra}SUMMARY:Weekly
            END:VEVENT
            END:VCALENDAR
            """.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var ve = Calendar.Load(ics)!.Events.Single();
        return ICalEventMapper.FromICalEvent(ve, Guid.NewGuid(), "zone-test", DateTime.UtcNow);
    }

    private static readonly DateTime TenInBerlinSummer = new(2026, 7, 15, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Europe/Berlin", "Europe/Berlin")]
    [InlineData("europe/berlin", "Europe/Berlin")]                         // wrong case
    [InlineData(" Europe/Berlin ", "Europe/Berlin")]
    [InlineData("/mozilla.org/20050126_1/Europe/Berlin", "Europe/Berlin")] // old Thunderbird/Lightning
    [InlineData("/citadel.org/20190914_1/Europe/Berlin", "Europe/Berlin")]
    [InlineData("W. Europe Standard Time", "Europe/Berlin")]               // Outlook/Exchange
    [InlineData("w. europe standard time", "Europe/Berlin")]
    [InlineData("America/Indiana/Indianapolis", "America/Indiana/Indianapolis")] // stays whole
    [InlineData("Eastern Standard Time", "America/New_York")]
    public void Canonical_IsTheTzDatabasesSpelling(string id, string expected) =>
        Assert.Equal(expected, TimeZones.Canonical(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Totally/Bogus")]
    [InlineData("Customized Time Zone")]
    [InlineData("/mozilla.org/Nowhere")]
    public void Canonical_IsNullForNoZone(string? id) => Assert.Null(TimeZones.Canonical(id));

    [Fact]
    public void Canonical_RefusesAnAbsurdlyLongId() =>
        Assert.Null(TimeZones.Canonical(new string('/', 10_000) + "Europe/Berlin"));

    [Theory]
    [InlineData("europe/berlin")]
    [InlineData("/mozilla.org/20050126_1/Europe/Berlin")]
    [InlineData("W. Europe Standard Time")]
    public void AnotherSpellingOfAKnownZone_ImportsAtTheRightTime_InThatZone(string tzid)
    {
        var e = Import(tzid, definition: null);

        Assert.Equal(TenInBerlinSummer, e.StartUtc);
        Assert.Equal("Europe/Berlin", e.TimeZoneId);
    }

    [Theory]
    [InlineData("(UTC+01:00) Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna", "Europe/Berlin")] // the first city that is a zone
    [InlineData("Customized Time Zone", "Europe/Berlin")]   // none named: the zone standing for a Windows one
    [InlineData("(UTC+01:00) Sarajevo, Skopje, Warsaw, Zagreb", "Europe/Warsaw")] // Sarajevo, Skopje: links to Belgrade
    public void OutlooksOwnDefinition_IsMatchedToTheRealZone(string tzid, string expectedZone)
    {
        var e = Import(tzid, CentralEurope);

        Assert.Equal(TenInBerlinSummer, e.StartUtc);
        Assert.Equal(expectedZone, e.TimeZoneId);
    }

    [Fact]
    public void AMatchedSeries_KeepsItsClockTimeAcrossDst()
    {
        var e = Import("Customized Time Zone", CentralEurope);

        // 10:00 in Berlin is 08:00Z in summer and 09:00Z after the clocks go back.
        var november = RecurrenceExpander.Expand(e.StartUtc, e.EndUtc!.Value, e.TimeZoneId, e.RRule!, new HashSet<DateTime>(),
            new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc), Assert.Single(november).StartUtc);
    }

    [Fact]
    public void ADefinitionNoZoneMatches_StillGivesTheRightInstant_AsAFloatingEvent()
    {
        var e = Import("Somewhere Odd", Invented);

        Assert.Equal(new DateTime(2026, 7, 15, 8, 30, 0, DateTimeKind.Utc), e.StartUtc);
        Assert.Null(e.TimeZoneId);
    }

    [Fact]
    public void ExclusionsAndOverrides_AreReadInTheSameZone()
    {
        var master = Calendar.Load($"""
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//test//EN
            {CentralEurope.Replace("{0}", "Customized Time Zone")}
            BEGIN:VEVENT
            UID:zone-test
            DTSTAMP:20260101T000000Z
            DTSTART;TZID="Customized Time Zone":20260715T100000
            RRULE:FREQ=WEEKLY
            EXDATE;TZID="Customized Time Zone":20260722T100000
            SUMMARY:Weekly
            END:VEVENT
            BEGIN:VEVENT
            UID:zone-test
            DTSTAMP:20260101T000000Z
            RECURRENCE-ID;TZID="Customized Time Zone":20260729T100000
            DTSTART;TZID="Customized Time Zone":20260729T120000
            SUMMARY:Moved
            END:VEVENT
            END:VCALENDAR
            """.Replace("\r\n", "\n").Replace("\n", "\r\n"))!.Events.ToList();

        Assert.Equal([new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc)], ICalEventMapper.ReadExDates(master[0]));
        Assert.Equal(new DateTime(2026, 7, 29, 8, 0, 0, DateTimeKind.Utc), ICalEventMapper.ReadRecurrenceIdUtc(master[1]));
    }

    [Fact]
    public void AStoredIdInAnotherSpelling_ExpandsInItsZone()
    {
        // A row written before ids were canonicalized. Ical.Net alone would read it as UTC.
        var start = new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Utc);
        var occurrences = RecurrenceExpander.Expand(start, start.AddHours(1), "europe/berlin", "FREQ=WEEKLY",
            new HashSet<DateTime>(),
            new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc), Assert.Single(occurrences).StartUtc);
    }

    [Fact]
    public void AHostileDefinition_IsReadWithinBounds()
    {
        // A secondly rule from year 1 would walk for ages; it is ignored, the DTSTART still counts.
        const string hostile = """
            BEGIN:VTIMEZONE
            TZID:{0}
            BEGIN:STANDARD
            DTSTART:00010101T000000
            TZOFFSETFROM:+0100
            TZOFFSETTO:+0100
            RRULE:FREQ=SECONDLY
            END:STANDARD
            BEGIN:DAYLIGHT
            DTSTART:00010101T000000
            TZOFFSETFROM:+0100
            TZOFFSETTO:+0100
            RRULE:FREQ=YEARLY;BYYEARDAY=1,2,3,4,5,6,7,8,9,10;COUNT=999999
            END:DAYLIGHT
            END:VTIMEZONE
            """;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var e = Import("Hostile", hostile);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Equal(new DateTime(2026, 7, 15, 9, 0, 0, DateTimeKind.Utc), e.StartUtc);
    }
}
