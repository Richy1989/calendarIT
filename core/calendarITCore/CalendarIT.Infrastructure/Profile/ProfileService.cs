using CalendarIT.Application.Profile;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Infrastructure.Profile;

/// <summary>EF Core-backed <see cref="IProfileService"/>. The avatar is stored on the user row.</summary>
public sealed class ProfileService(AppDbContext db) : IProfileService
{
    // Views the client may persist; anything else is rejected so the DB stays clean.
    private static readonly HashSet<string> KnownViews =
        new(StringComparer.Ordinal) { "dayGridMonth", "timeGridWeek", "timeGridDay" };

    public async Task<ProfileDto?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Email, u.AvatarData, u.AvatarContentType, u.DefaultCalendarView })
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return null;
        }

        string? dataUrl = user.AvatarData is { Length: > 0 } bytes && !string.IsNullOrEmpty(user.AvatarContentType)
            ? $"data:{user.AvatarContentType};base64,{Convert.ToBase64String(bytes)}"
            : null;

        return new ProfileDto(user.Email, dataUrl, user.DefaultCalendarView);
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
