using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CalendarIT.Infrastructure.Calendars;

namespace CalendarIT.Tests;

/// <summary>
/// testdata/recurrence-vectors.json: rules with the occurrences this server expands them to. The
/// Android app expands repeats on the device and checks itself against the same file, so a phone
/// and the web never disagree about when something happens. Set UPDATE_RECURRENCE_VECTORS=1 to
/// rewrite the expected lists from this expander (then review the diff).
/// </summary>
public sealed class RecurrenceVectorsTests
{
    private static string VectorsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "testdata", "recurrence-vectors.json")))
        {
            dir = dir.Parent;
        }
        return Path.Combine(dir?.FullName ?? throw new FileNotFoundException("testdata/recurrence-vectors.json"), "testdata", "recurrence-vectors.json");
    }

    private static DateTime Utc(string iso) =>
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static List<string> Expand(JsonNode v) =>
        RecurrenceExpander.Expand(
                Utc(v["start"]!.GetValue<string>()),
                Utc(v["end"]!.GetValue<string>()),
                v["timeZone"]?.GetValue<string>(),
                v["rrule"]!.GetValue<string>(),
                v["exDates"]!.AsArray().Select(d => ExDates.TruncateToSeconds(Utc(d!.GetValue<string>()))).ToHashSet(),
                Utc(v["from"]!.GetValue<string>()),
                Utc(v["to"]!.GetValue<string>()),
                v["allDay"]!.GetValue<bool>())
            .Select(o => Iso(o.StartUtc))
            .ToList();

    [Fact]
    public void TheServer_ExpandsEveryVector_AsRecorded()
    {
        var path = VectorsPath();
        var vectors = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        if (Environment.GetEnvironmentVariable("UPDATE_RECURRENCE_VECTORS") == "1")
        {
            foreach (var v in vectors)
            {
                v!["expected"] = new JsonArray(Expand(v).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
            }
            File.WriteAllText(path, vectors.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return;
        }
        foreach (var v in vectors)
        {
            var expected = v!["expected"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
            Assert.True(expected.SequenceEqual(Expand(v)), $"{v["name"]}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", Expand(v))}]");
        }
    }
}
