using CalendarIT.Domain;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Persistence;
using MailKit;
using MailKit.Search;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// Ticks every minute and, for each connected mail account whose scan interval has elapsed,
/// reads new iMIP messages from the inbox: REPLY messages apply a guest's Accept/Decline to the
/// matching event, while REQUEST/CANCEL messages (invitations others send us) add or withdraw
/// the event on the recipient's own calendar. Idempotent via an IMAP UID highwater mark stored
/// on the account, so each message is processed once; the mailbox is only read, never modified.
/// </summary>
[DisallowConcurrentExecution]
public sealed class InvitationInboxJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<InvitationInboxJob> logger) : IJob
{
    private MailConnectionOptions connectionOptions = new();

    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<MailAccountService>();
        var replies = scope.ServiceProvider.GetRequiredService<IInvitationReplyService>();
        var invitations = scope.ServiceProvider.GetRequiredService<IIncomingInvitationService>();
        connectionOptions = scope.ServiceProvider.GetService<MailConnectionOptions>() ?? new MailConnectionOptions();

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
                await ScanAccountAsync(account, accounts, replies, invitations, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A broken account (bad password, host down) must not stall the others.
                logger.LogWarning(ex, "Inbox scan failed for user {UserId}: {Reason}",
                    account.UserId, MailConnections.Describe(ex));
            }

            // Record the attempt even on failure so a failing account isn't retried every tick.
            account.LastScanAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ScanAccountAsync(
        MailAccount account, MailAccountService accounts, IInvitationReplyService replies,
        IIncomingInvitationService invitations, CancellationToken cancellationToken)
    {
        var password = accounts.Unprotect(account);
        if (password is null)
        {
            return; // no usable password (never set, or protection keys lost)
        }

        // ImapHost is non-null here — candidates are filtered on ImapHost != null in Execute.
        using var client = await MailConnections.OpenImapAsync(account, password, connectionOptions, cancellationToken);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        // A server-side SEARCH keeps the download to just the iMIP messages, even on the first
        // run over a full mailbox: a guest's REPLY, or an invitation others send us
        // (REQUEST/CANCEL). Once we have a highwater mark for this same UIDVALIDITY, restrict to
        // newer UIDs; a UIDVALIDITY change means the mark is stale, so start over.
        SearchQuery query = SearchQuery.BodyContains("METHOD:REPLY")
            .Or(SearchQuery.BodyContains("METHOD:REQUEST"))
            .Or(SearchQuery.BodyContains("METHOD:CANCEL"));
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
            // One unprocessable message must not stall the mailbox. Without this, the throw
            // escaped to Execute's per-account catch, the highwater never advanced, and that
            // same message was re-fetched and re-thrown on every scan — with everything behind
            // it in the batch never processed. It is skipped (logged) and the mark moves on.
            try
            {
                await ProcessMessageAsync(account, await inbox.GetMessageAsync(uid, cancellationToken),
                    replies, invitations, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Skipping unprocessable message UID {Uid} for user {UserId}", uid.Id, account.UserId);
            }

            highest = Math.Max(highest, uid.Id);
        }

        await client.DisconnectAsync(quit: true, cancellationToken);

        // Advance the highwater mark only after the batch was read end to end (a connection
        // failure mid-batch leaves it untouched so those UIDs are retried next scan).
        account.ImapUidValidity = inbox.UidValidity;
        if (highest > 0)
        {
            account.ImapLastUid = highest;
        }
    }

    /// <summary>Routes one message: a guest's RSVP (REPLY), or an invitation aimed at us
    /// (REQUEST/CANCEL) — whichever parser recognises it. Unrecognised messages are ignored, and
    /// so are a REPLY or CANCEL whose iCalendar body claims a sender the message didn't come
    /// from; such a REQUEST (a forward) is delivered as pending.</summary>
    private async Task ProcessMessageAsync(
        MailAccount account, MimeMessage message, IInvitationReplyService replies,
        IIncomingInvitationService invitations, CancellationToken cancellationToken)
    {
        var reply = ImipReplyParser.TryParse(message);
        if (reply is not null)
        {
            // The body says this guest replied; the headers have to agree, or anyone could mark
            // anyone as having accepted.
            if (!ImipMime.IsFromClaimedSender(message, reply.AttendeeEmail))
            {
                logger.LogWarning(
                    "Ignoring REPLY for event {Uid}: body claims attendee {Email}, message is not from them",
                    reply.Uid, reply.AttendeeEmail);
                return;
            }

            if (await replies.ApplyReplyAsync(account.UserId, reply, cancellationToken))
            {
                logger.LogInformation(
                    "Applied {Status} from {Email} to event {Uid} for user {UserId}",
                    reply.Status, reply.AttendeeEmail, reply.Uid, account.UserId);
            }
        }
        else if (ImipRequestParser.TryParse(message) is { } request)
        {
            // An unverified CANCEL is how a stranger deletes from your calendar, so it's ignored.
            // An unverified REQUEST is usually a forward (From is the forwarder): it's delivered,
            // but only ever as a pending invitation the user accepts or declines.
            var verified = ImipMime.IsFromClaimedSender(message, request.OrganizerEmail);
            if (!verified && request.Method == ImipRequestMethod.Cancel)
            {
                logger.LogWarning(
                    "Ignoring CANCEL for event {Uid}: body claims organizer {Organizer}, message is not from them",
                    request.Uid, request.OrganizerEmail);
                return;
            }

            if (await invitations.ApplyRequestAsync(account.UserId, request, verified, cancellationToken))
            {
                if (verified)
                {
                    logger.LogInformation(
                        "Applied incoming {Method} for event {Uid} from {Organizer} to user {UserId}",
                        request.Method, request.Uid, request.OrganizerEmail, account.UserId);
                }
                else
                {
                    logger.LogInformation(
                        "Added forwarded invitation {Uid} (organizer {Organizer}, sent by {From}) to user {UserId} as pending",
                        request.Uid, request.OrganizerEmail, message.From.Mailboxes.FirstOrDefault()?.Address, account.UserId);
                }
            }
        }
    }
}
