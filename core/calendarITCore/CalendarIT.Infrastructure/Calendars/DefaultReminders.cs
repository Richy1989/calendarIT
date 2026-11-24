using CalendarIT.Application;
using CalendarIT.Application.Calendars;
using CalendarIT.Domain;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// A calendar's default reminders as stored: "minutes:channel" pairs, ';'-separated
/// ("15:WebPush;1440:Email"). One column rather than a table — a handful of values, read with the
/// calendar, written as a whole. Reading never throws: an unreadable pair is skipped.
/// </summary>
public static class DefaultReminders
{
    private const int MaxMinutes = 40320; // as ReminderInput: up to 4 weeks before

    public static IReadOnlyList<ReminderDto> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }
        var result = new List<ReminderDto>();
        foreach (var pair in stored.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var minutes) && minutes is >= 0 and <= MaxMinutes
                && Enum.TryParse<ReminderChannel>(parts[1], ignoreCase: true, out var channel)
                && Enum.IsDefined(channel))
            {
                result.Add(new ReminderDto(minutes, channel.ToString()));
            }
        }
        return result;
    }

    /// <summary>The value to store for <paramref name="reminders"/> (null for none), in order of
    /// time, duplicates dropped. Throws <see cref="InvalidInputException"/> for an unknown channel.</summary>
    public static string? Format(IReadOnlyList<ReminderInput> reminders)
    {
        var pairs = new List<(int Minutes, ReminderChannel Channel)>();
        foreach (var r in reminders)
        {
            if (!Enum.TryParse<ReminderChannel>(r.Channel, ignoreCase: true, out var channel) || !Enum.IsDefined(channel)
                || int.TryParse(r.Channel, out _))
            {
                throw new InvalidInputException($"Unknown reminder channel \"{r.Channel}\".");
            }
            pairs.Add((r.MinutesBefore, channel));
        }
        var distinct = pairs.Distinct().OrderBy(p => p.Minutes).ThenBy(p => p.Channel).ToList();
        return distinct.Count == 0 ? null : string.Join(';', distinct.Select(p => $"{p.Minutes}:{p.Channel}"));
    }
}
