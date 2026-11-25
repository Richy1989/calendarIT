using CalendarIT.Application.Profile;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Infrastructure.Profile;

/// <summary>EF Core-backed <see cref="IProfileService"/>. The avatar is stored on the user row.</summary>
public sealed class ProfileService(AppDbContext db) : IProfileService
{
    // Views the client may persist; anything else is rejected so the DB stays clean.
    // "agendaList" is the client's own upcoming list; "listMonth" is its retired
    // predecessor, still accepted so previously saved values keep working.
    private static readonly HashSet<string> KnownViews =
        new(StringComparer.Ordinal) { "dayGridMonth", "timeGridWeek", "timeGridDay", "agendaList", "listMonth" };

    // Days a week may start on. Two for now; the column stores the name, so adding Saturday is
    // a change here rather than a migration.
    private static readonly HashSet<string> KnownWeekStarts =
        new(StringComparer.Ordinal) { "sunday", "monday" };

    private static readonly HashSet<string> KnownThemes =
        new(StringComparer.Ordinal) { "corner-store", "midnight", "daylight", "graphite" };

    public async Task<ProfileDto?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.Email, u.AvatarData, u.AvatarContentType, u.DefaultCalendarView, u.Use24HourClock, u.WeekStart,
                u.Theme, u.AccentColor, u.HighlightColor,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return null;
        }

        string? dataUrl = user.AvatarData is { Length: > 0 } bytes && !string.IsNullOrEmpty(user.AvatarContentType)
            ? $"data:{user.AvatarContentType};base64,{Convert.ToBase64String(bytes)}"
            : null;

        return new ProfileDto(user.Email, dataUrl, user.DefaultCalendarView, user.Use24HourClock, user.WeekStart,
            user.Theme, user.AccentColor, user.HighlightColor);
    }

    public async Task<bool> SetAppearanceAsync(
        Guid userId, string? theme, string? accentColor, string? highlightColor, CancellationToken cancellationToken = default)
    {
        string? normalizedTheme = null;
        if (theme is not null)
        {
            normalizedTheme = theme.Trim().ToLowerInvariant();
            if (!KnownThemes.Contains(normalizedTheme))
            {
                return false;
            }
        }
        if (!TryNormalizeColor(accentColor, out var accent) || !TryNormalizeColor(highlightColor, out var highlight))
        {
            return false;
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return false;
        }
        user.Theme = normalizedTheme;
        user.AccentColor = accent;
        user.HighlightColor = highlight;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Null stays null (the theme's own color); otherwise only "#rrggbb", stored lowercase.
    /// The value lands in a CSS custom property, so nothing else may get through.</summary>
    private static bool TryNormalizeColor(string? color, out string? normalized)
    {
        normalized = null;
        if (color is null)
        {
            return true;
        }
        var c = color.Trim();
        if (c.Length != 7 || c[0] != '#' || !c.Skip(1).All(Uri.IsHexDigit))
        {
            return false;
        }
        normalized = c.ToLowerInvariant();
        return true;
    }

    public async Task<bool> SetWeekStartAsync(Guid userId, string? weekStart, CancellationToken cancellationToken = default)
    {
        // Null is a choice ("follow my browser"), not a missing value, so it has to pass
        // validation — but only a real null, never an empty string that arrived by accident.
        string? normalized = null;
        if (weekStart is not null)
        {
            normalized = weekStart.Trim().ToLowerInvariant();
            if (!KnownWeekStarts.Contains(normalized))
            {
                return false;
            }
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return false;
        }
        user.WeekStart = normalized;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task SetClockFormatAsync(Guid userId, bool use24Hour, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return;
        }
        user.Use24HourClock = use24Hour;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> SetDefaultViewAsync(Guid userId, string view, CancellationToken cancellationToken = default)
    {
        if (!KnownViews.Contains(view))
        {
            return false;
        }
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return false;
        }
        user.DefaultCalendarView = view;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task SetAvatarAsync(Guid userId, byte[] data, string contentType, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");
        user.AvatarData = data;
        user.AvatarContentType = contentType;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearAvatarAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return;
        }
        user.AvatarData = null;
        user.AvatarContentType = null;
        await db.SaveChangesAsync(cancellationToken);
    }
}
