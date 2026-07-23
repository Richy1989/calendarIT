using CalendarIT.Domain;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Persistence;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// Ticks every minute and, for each connected mail account whose scan interval has elapsed,
/// reads new iMIP REPLY messages from the inbox and applies the guest's Accept/Decline to the
/// matching event. Idempotent via an IMAP UID highwater mark stored on the account, so each
/// reply is processed once; the mailbox is only read, never modified.
/// </summary>
[DisallowConcurrentExecution]
public sealed class InvitationInboxJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<InvitationInboxJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<MailAccountService>();
        var replies = scope.ServiceProvider.GetRequiredService<IInvitationReplyService>();

        // Only accounts with an IMAP server and scanning enabled are candidates.
        var candidates = await db.MailAccounts
            .Where(a => a.ImapHost != null && a.ScanIntervalMinutes > 0)
            .ToListAsync(cancellationToken);

        foreach (var account in candidates)
        {
            if (account.LastScanAt is { } last && (now - last).TotalMinutes < account.ScanIntervalMinutes)
            {
                continue; // not due yet
            }

            try
            {
                await ScanAccountAsync(account, accounts, replies, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A broken account (bad password, host down) must not stall the others.
                logger.LogWarning(ex, "Inbox scan failed for user {UserId}", account.UserId);
            }

            // Record the attempt even on failure so a failing account isn't retried every tick.
            account.LastScanAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ScanAccountAsync(
        MailAccount account, MailAccountService accounts, IInvitationReplyService replies, CancellationToken cancellationToken)
    {
        var password = accounts.Unprotect(account);
        if (password is null)
        {
            return; // no usable password (never set, or protection keys lost)
        }

        using var client = new ImapClient();
        var socketOptions = account.ImapUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(account.ImapHost, account.ImapPort, socketOptions, cancellationToken);
        await client.AuthenticateAsync(account.Username, password, cancellationToken);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        // A server-side SEARCH keeps the download to just the iMIP replies, even on the first
        // run over a full mailbox. Once we have a highwater mark for this same UIDVALIDITY,
        // restrict to newer UIDs; a UIDVALIDITY change means the mark is stale, so start over.
        SearchQuery query = SearchQuery.BodyContains("METHOD:REPLY");
        var sameStore = account.ImapUidValidity == inbox.UidValidity;
        if (sameStore && account.ImapLastUid is { } lastUid)
        {
            query = query.And(SearchQuery.Uids(
                new UniqueIdRange(new UniqueId((uint)lastUid + 1), UniqueId.MaxValue)));
        }

        var uids = (await inbox.SearchAsync(query, cancellationToken))
            .OrderBy(u => u.Id)
            .ToList();

        var highest = sameStore && account.ImapLastUid is { } start ? (uint)start : 0u;
        foreach (var uid in uids)
        {
            var message = await inbox.GetMessageAsync(uid, cancellationToken);
            var reply = ImipReplyParser.TryParse(message);
            if (reply is not null && await replies.ApplyReplyAsync(account.UserId, reply, cancellationToken))
            {
                logger.LogInformation(
                    "Applied {Status} from {Email} to event {Uid} for user {UserId}",
                    reply.Status, reply.AttendeeEmail, reply.Uid, account.UserId);
            }
            highest = Math.Max(highest, uid.Id);
        }

        await client.DisconnectAsync(quit: true, cancellationToken);

        // Advance the highwater mark only after the batch was read end to end (a mid-batch
        // failure leaves it untouched so those UIDs are retried next scan).
        account.ImapUidValidity = inbox.UidValidity;
        if (highest > 0)
        {
            account.ImapLastUid = highest;
        }
    }
}
