using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CalendarIT.Infrastructure.Auth;

/// <summary>
/// The server's admin: the person who activates new accounts. The first account on a server
/// becomes it (it activates itself — there is nobody else to do it); the operator can name
/// another with the <c>make-admin</c> command. Checked against the database on every use, never
/// read from a token, so taking the role away takes effect at once.
/// </summary>
public static class AdminRole
{
    public const string Name = "Admin";

    /// <summary>Written as a plain row: <c>UserManager.AddToRoleAsync</c> re-validates the whole
    /// user, which an old or hand-made row (no security stamp) fails — at start-up, fatally.</summary>
    public static async Task GrantAsync(AppDbContext db, Guid userId)
    {
        var roleId = await db.Roles.Where(r => r.NormalizedName == "ADMIN").Select(r => (Guid?)r.Id).SingleOrDefaultAsync();
        if (roleId is null)
        {
            var role = new IdentityRole<Guid>
            {
                Id = Guid.NewGuid(), Name = Name, NormalizedName = Name.ToUpperInvariant(),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
            };
            db.Roles.Add(role);
            roleId = role.Id;
        }
        if (!await db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == roleId))
        {
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = userId, RoleId = roleId.Value });
        }
        await db.SaveChangesAsync();
    }

    public static async Task<bool> IsAdminAsync(AppDbContext db, Guid userId, CancellationToken ct = default) =>
        await (from ur in db.UserRoles
               join r in db.Roles on ur.RoleId equals r.Id
               where ur.UserId == userId && r.NormalizedName == "ADMIN"
               select ur).AnyAsync(ct);

    /// <summary>
    /// A server whose accounts predate the admin role gets one: the oldest active account (by
    /// sign-up time, else by its first calendar). Runs at start-up; a no-op once anyone is admin.
    /// </summary>
    public static async Task EnsureOneAsync(IServiceProvider services, CancellationToken ct = default)
    {
        try
        {
            await EnsureOneCoreAsync(services, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never a reason not to start: the operator can name one with make-admin.
            services.GetRequiredService<ILoggerFactory>().CreateLogger("CalendarIT.Admin").LogWarning(ex,
                "Couldn't pick an admin for this server. Name one with: dotnet calendarITCore.dll make-admin <email>");
        }
    }

    private static async Task EnsureOneCoreAsync(IServiceProvider services, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var adminRole = await db.Roles.Where(r => r.NormalizedName == "ADMIN").Select(r => (Guid?)r.Id).SingleOrDefaultAsync(ct);
        if (adminRole is { } roleId && await db.UserRoles.AnyAsync(ur => ur.RoleId == roleId, ct))
        {
            return;
        }

        var candidates = await db.Users.AsNoTracking()
            .Where(u => u.EmailConfirmed)
            .Select(u => new
            {
                u.Id,
                u.CreatedAtUtc,
                FirstCalendar = db.Calendars.Where(c => c.OwnerUserId == u.Id).Min(c => (DateTime?)c.CreatedAt),
            })
            .ToListAsync(ct);
        var oldest = candidates
            .OrderBy(c => c.CreatedAtUtc ?? c.FirstCalendar ?? DateTime.MaxValue)
            .FirstOrDefault();
        if (oldest is null)
        {
            return; // no accounts yet: the first sign-up becomes admin
        }

        await GrantAsync(db, oldest.Id);
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("CalendarIT.Admin")
            .LogInformation("Made user {UserId}, the oldest account, this server's admin", oldest.Id);
    }
}
