using CalendarIT.Application.Auth;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace CalendarIT.Infrastructure.Auth;

/// <summary>Tells the server's admins that someone signed up.</summary>
public interface IAdminSignupNotifier
{
    /// <summary>Queues the notice; never throws — a sign-up never fails over it.</summary>
    Task NotifyAsync(string activation, CancellationToken cancellationToken = default);
}

/// <summary>
/// Mails each admin about new sign-ups — through the server's mail account when there is one,
/// else through the admin's own connected account, to themselves. Bundled: at most one mail per
/// admin per <see cref="Cooldown"/>, listing everyone waiting, so a burst of sign-ups (spam
/// included) is one mail, not a flood. Without any mail account the web app's badge still shows
/// who waits (Settings → Users).
/// </summary>
public sealed class AdminSignupNotifier(
    AppDbContext db,
    IMailOutbox outbox,
    IOptions<AuthOptions> authOptions,
    ILogger<AdminSignupNotifier> logger,
    IMemoryCache? cache = null) : IAdminSignupNotifier
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    /// <summary>Accounts named in one mail; the rest are counted.</summary>
    private const int MaxListed = 20;

    public async Task NotifyAsync(string activation, CancellationToken cancellationToken = default)
    {
        try
        {
            await NotifyCoreAsync(activation, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't queue the new-account notice for the admins");
        }
    }

    private async Task NotifyCoreAsync(string activation, CancellationToken cancellationToken)
    {
        var admins = await (from u in db.Users
                            join ur in db.UserRoles on u.Id equals ur.UserId
                            join r in db.Roles on ur.RoleId equals r.Id
                            where r.NormalizedName == "ADMIN" && u.EmailConfirmed && u.DisabledAtUtc == null && u.Email != null
                            select new { u.Id, u.Email }).ToListAsync(cancellationToken);
        if (admins.Count == 0)
        {
            return;
        }
        var waiting = await db.Users.AsNoTracking()
            .Where(u => !u.EmailConfirmed)
            .Select(u => new { u.Email, u.CreatedAtUtc })
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0)
        {
            return;
        }
        var listed = waiting
            .OrderByDescending(u => u.CreatedAtUtc ?? DateTime.MinValue)
            .Take(MaxListed)
            .Select(u => "  • " + u.Email)
            .ToList();
        if (waiting.Count > MaxListed)
        {
            listed.Add($"  … and {waiting.Count - MaxListed} more");
        }

        var settings = string.IsNullOrWhiteSpace(authOptions.Value.PublicBaseUrl)
            ? "Settings → Users"
            : $"Settings → Users: {authOptions.Value.PublicBaseUrl!.TrimEnd('/')}/?settings=users";
        var how = activation == AccountActivation.Email
            ? "New accounts activate themselves with the link mailed to them. If you don't know someone, remove them before they do."
            : "They can't sign in until you activate them.";

        foreach (var admin in admins)
        {
            if (cache is not null)
            {
                var key = $"admin-signup-notice:{admin.Id:N}";
                if (cache.TryGetValue(key, out _))
                {
                    continue; // one went out moments ago; the badge in the app shows the rest
                }
                cache.Set(key, true, Cooldown);
            }

            var message = new MimeMessage();
            message.To.Add(MailboxAddress.Parse(admin.Email!));
            message.Subject = waiting.Count == 1
                ? "A new account is waiting on 24seven"
                : $"{waiting.Count} new accounts are waiting on 24seven";
            message.Body = new TextPart("plain")
            {
                Text = $"""
                    Someone signed up on your 24seven server. Waiting for activation:

                    {string.Join("\n", listed)}

                    {how}

                    {settings}
                    """,
            };
            // The server's account first; else the admin's own, to themselves.
            if (await outbox.QueueSystemAsync(admin.Id, message, OutboxKind.AdminNotice, cancellationToken)
                || await outbox.QueueAsync(admin.Id, message, OutboxKind.AdminNotice, cancellationToken))
            {
                logger.LogInformation("Told admin {AdminId} about {Count} account(s) waiting for activation", admin.Id, waiting.Count);
            }
        }
    }
}
