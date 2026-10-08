using Ical.Net;
using Ical.Net.DataTypes;
using Ical.Net.Serialization.DataTypes;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// The one place that decides whether an RRULE is something we store.
///
/// A rule is text from outside — the editor, an .ics import, a CalDAV PUT, an emailed invitation —
/// and every stored rule is re-parsed on every read: the calendar view, search, the reminder job.
/// One unparseable rule therefore used to take down that user's calendar, and (through the
/// reminder job, which reads everyone's) every user's reminders. So nothing reaches the database
/// without passing through here.
///
/// Sub-hourly frequencies are refused outright. No calendar UI offers them, and a FREQ=SECONDLY
/// series expands to 86,400 occurrences a day — a few kilobytes of invitation that costs the
/// server megabytes on every page load.
/// </summary>
public static class RecurrenceRules
{
    /// <summary>Stored column width (see <c>AppDbContext</c>).</summary>
    public const int MaxLength = 1000;

    /// <summary>
    /// Validates a rule typed into the editor (API input). Null/blank is a one-off event.
    /// Returns false with a user-facing reason when the rule can't be stored.
    /// </summary>
    public static bool TryNormalize(string? raw, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["RRULE:".Length..];
        }
        if (trimmed.Length > MaxLength)
        {
            error = $"The repeat rule is longer than {MaxLength} characters.";
            return false;
        }

        RecurrenceRule rule;
        try
        {
            rule = new RecurrenceRule(trimmed);
        }
        catch (Exception)
        {
            error = "The repeat rule isn't a valid iCalendar RRULE.";
            return false;
        }

        if (!IsSupported(rule))
        {
            error = "Repeating more often than hourly isn't supported.";
            return false;
        }

        normalized = trimmed;
        return true;
    }

    /// <summary>
    /// The rule an incoming VEVENT carries, as we'd store it — or null when it has none, or one we
    /// won't keep (too frequent, too long, or not serializable). External input never fails the
    /// whole message over a rule: the event is kept as a one-off instead.
    /// </summary>
    public static string? FromExternal(RecurrenceRule? rule)
    {
        if (rule is null || !IsSupported(rule))
        {
            return null;
        }
        string? text;
        try
        {
            text = new RecurrenceRuleSerializer().SerializeToString(rule);
        }
        catch (Exception)
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(text) || text.Length > MaxLength ? null : text;
    }

    private static bool IsSupported(RecurrenceRule rule) =>
        rule.Frequency is not (FrequencyType.Secondly or FrequencyType.Minutely);
}
