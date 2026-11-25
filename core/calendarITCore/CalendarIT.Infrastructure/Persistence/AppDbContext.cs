using CalendarIT.Domain;
using CalendarIT.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the whole application. Extends the ASP.NET Core Identity schema
/// (users, roles, claims) with our own tables. Calendar/event entities are added in
/// later phases; this phase establishes accounts and refresh tokens.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Calendar> Calendars => Set<Calendar>();

    public DbSet<CalendarEvent> Events => Set<CalendarEvent>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Reminder> Reminders => Set<Reminder>();

    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();

    public DbSet<ReminderSnooze> ReminderSnoozes => Set<ReminderSnooze>();

    public DbSet<DeletedResource> DeletedResources => Set<DeletedResource>();

    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    public DbSet<TaskReminder> TaskReminders => Set<TaskReminder>();

    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    public DbSet<MailAccount> MailAccounts => Set<MailAccount>();

    public DbSet<Attendee> Attendees => Set<Attendee>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(u => u.AvatarContentType).HasMaxLength(100);
            entity.Property(u => u.WeekStart).HasMaxLength(16);
            entity.Property(u => u.Theme).HasMaxLength(16);
            entity.Property(u => u.AccentColor).HasMaxLength(7);
            entity.Property(u => u.HighlightColor).HasMaxLength(7);
        });

        builder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(t => t.Id);

            entity.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(t => t.TokenHash).IsUnique();

            entity.Property(t => t.ReplacedByTokenHash).HasMaxLength(128);
            entity.Property(t => t.UserAgent).HasMaxLength(300);
            entity.Property(t => t.IpAddress).HasMaxLength(64);
            entity.HasIndex(t => t.SessionId);

            // One user has many refresh tokens; deleting the user clears them.
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(t => t.UserId);
        });

        builder.Entity<Calendar>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(200).IsRequired();
            entity.Property(c => c.Color).HasMaxLength(32);
            entity.Property(c => c.TimeZoneId).HasMaxLength(64);
            entity.Property(c => c.DefaultReminders).HasMaxLength(200);
            entity.Property(c => c.SourceUrl).HasMaxLength(2000);
            entity.Property(c => c.LastFetchError).HasMaxLength(500);
            entity.Property(c => c.SourceETag).HasMaxLength(200);
            entity.Property(c => c.SourceLastModified).HasMaxLength(100);
            entity.HasIndex(c => c.OwnerUserId);
            entity.HasIndex(c => c.NextFetchUtc); // the refresh job's "what is due" query

            // Deleting the category leaves the calendar without a default (its events uncategorized).
            entity.HasOne(c => c.DefaultCategory)
                .WithMany()
                .HasForeignKey(c => c.DefaultCategoryId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(c => c.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CalendarEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Uid).HasMaxLength(255).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(8000);
            entity.Property(e => e.Location).HasMaxLength(500);
            entity.Property(e => e.Color).HasMaxLength(32);
            entity.Property(e => e.TimeZoneId).HasMaxLength(64);
            entity.Property(e => e.RRule).HasMaxLength(1000);
            entity.Property(e => e.InvitationStatus).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.OrganizerEmail).HasMaxLength(320);

            entity.HasIndex(e => new { e.CalendarId, e.StartUtc });

            // A UID names one resource per calendar (a CalDAV href is built from it). Overrides
            // share their master's UID, so they're left out of this index and kept unique per
            // occurrence instead. Without it, two concurrent PUTs or imports of the same UID both
            // inserted, and every UID lookup after that threw on the duplicate. The filter is
            // plain SQL both providers accept.
            entity.HasIndex(e => new { e.CalendarId, e.Uid })
                .IsUnique()
                .HasFilter("\"SeriesMasterId\" IS NULL");
            entity.HasIndex(e => new { e.SeriesMasterId, e.RecurrenceIdUtc })
                .IsUnique()
                .HasFilter("\"SeriesMasterId\" IS NOT NULL");

            entity.HasOne(e => e.SeriesMaster)
                .WithMany(e => e.Overrides)
                .HasForeignKey(e => e.SeriesMasterId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Calendar)
                .WithMany(c => c.Events)
                .HasForeignKey(e => e.CalendarId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting a category leaves its events uncategorized (default color).
            entity.HasIndex(e => e.CategoryId);
            entity.HasOne(e => e.Category)
                .WithMany(c => c.Events)
                .HasForeignKey(e => e.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Category>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(100).IsRequired();
            entity.Property(c => c.Color).HasMaxLength(32).IsRequired();
            entity.HasIndex(c => c.OwnerUserId);
            // Names are unique per user (the service checks case-insensitively; this is the backstop).
            entity.HasIndex(c => new { c.OwnerUserId, c.Name }).IsUnique();

            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(c => c.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Reminder>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Channel).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(r => r.EventId);

            entity.HasOne(r => r.Event)
                .WithMany(e => e.Reminders)
                .HasForeignKey(r => r.EventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<NotificationLog>(entity =>
        {
            entity.HasKey(n => n.Id);
            entity.HasIndex(n => new { n.ReminderId, n.OccurrenceStartUtc }).IsUnique();
        });

        builder.Entity<TaskItem>(entity =>
        {
            entity.ToTable("Tasks");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Uid).HasMaxLength(255).IsRequired();
            entity.Property(t => t.Title).HasMaxLength(500).IsRequired();
            entity.Property(t => t.Notes).HasMaxLength(8000);
            entity.Property(t => t.TimeZoneId).HasMaxLength(64);
            entity.Property(t => t.RRule).HasMaxLength(1000);
            entity.Property(t => t.RelatedToUid).HasMaxLength(255);
            entity.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(t => new { t.CalendarId, t.Uid }).IsUnique();
            entity.HasIndex(t => new { t.CalendarId, t.Status });
            entity.HasOne(t => t.Calendar)
                .WithMany()
                .HasForeignKey(t => t.CalendarId)
                .OnDelete(DeleteBehavior.Cascade);
            // Deleting a task deletes its subtasks; done in code too, since a self-reference
            // can't cascade in every provider.
            entity.HasOne(t => t.Parent)
                .WithMany(t => t.Subtasks)
                .HasForeignKey(t => t.ParentId)
                .OnDelete(DeleteBehavior.ClientCascade);
        });

        builder.Entity<TaskReminder>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Channel).HasConversion<string>().HasMaxLength(16);
            entity.HasOne(r => r.Task)
                .WithMany(t => t.Reminders)
                .HasForeignKey(r => r.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DeletedResource>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Uid).HasMaxLength(255).IsRequired();
            entity.HasIndex(d => new { d.CalendarId, d.DeletedAtUtc });
            // A deleted calendar takes its records along: there is nothing left to sync.
            entity.HasOne<Calendar>().WithMany().HasForeignKey(d => d.CalendarId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ReminderSnooze>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.ReminderId, s.OccurrenceStartUtc }).IsUnique();
            entity.HasIndex(s => s.DueAtUtc);
            entity.HasOne(s => s.Reminder)
                .WithMany()
                .HasForeignKey(s => s.ReminderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PushSubscription>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Endpoint).HasMaxLength(2000).IsRequired();
            entity.Property(s => s.P256dh).HasMaxLength(255).IsRequired();
            entity.Property(s => s.Auth).HasMaxLength(255).IsRequired();
            entity.Property(s => s.UserAgent).HasMaxLength(500);
            // One subscription per endpoint; re-subscribing the same browser upserts this row.
            entity.HasIndex(s => s.Endpoint).IsUnique();
            entity.HasIndex(s => s.UserId);

            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MailAccount>(entity =>
        {
            // One account per user: the user id doubles as the key.
            entity.HasKey(a => a.UserId);
            entity.Property(a => a.Address).HasMaxLength(320).IsRequired();
            entity.Property(a => a.FromAddress).HasMaxLength(320);
            entity.Property(a => a.SmtpHost).HasMaxLength(255).IsRequired();
            entity.Property(a => a.ImapHost).HasMaxLength(255);
            entity.Property(a => a.Username).HasMaxLength(320).IsRequired();
            entity.Property(a => a.PasswordProtected).HasMaxLength(2000).IsRequired();
            entity.Property(a => a.ScanIntervalMinutes).HasDefaultValue(5);

            entity.HasOne<ApplicationUser>()
                .WithOne()
                .HasForeignKey<MailAccount>(a => a.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Kind).HasConversion<string>().HasMaxLength(32);
            entity.Property(m => m.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(m => m.Recipient).HasMaxLength(320).IsRequired();
            entity.Property(m => m.Subject).HasMaxLength(500).IsRequired();
            entity.Property(m => m.LastError).HasMaxLength(500);
            // The dispatcher's scan: what's due, oldest first.
            entity.HasIndex(m => new { m.Status, m.NextAttemptAtUtc });
            entity.HasIndex(m => new { m.UserId, m.CreatedAtUtc });

            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Attendee>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Email).HasMaxLength(320).IsRequired();
            entity.Property(a => a.Name).HasMaxLength(200);
            entity.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(a => a.EventId);

            entity.HasOne(a => a.Event)
                .WithMany(e => e.Attendees)
                .HasForeignKey(a => a.EventId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RecordDeletedResources();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RecordDeletedResources();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Notes every event (series master or one-off — a CalDAV resource) leaving a calendar in this
    /// save: deleted, or moved to another calendar. Here, rather than at each place that deletes,
    /// so no path — the API, CalDAV, an invitation cancel, a subscription refresh — can forget it,
    /// and a phone syncing incrementally never keeps an event that is gone.
    /// </summary>
    private void RecordDeletedResources()
    {
        ChangeTracker.DetectChanges();
        var now = DateTime.UtcNow;
        var gone = new List<DeletedResource>();
        // A calendar deleted in the same save takes its events along — there is no collection
        // left to report them in (and the record would point at a row that no longer exists).
        var deletedCalendars = ChangeTracker.Entries<Calendar>()
            .Where(c => c.State == EntityState.Deleted)
            .Select(c => c.Entity.Id)
            .ToHashSet();
        foreach (var entry in ChangeTracker.Entries<CalendarEvent>())
        {
            if (entry.Property(e => e.SeriesMasterId).OriginalValue is not null)
            {
                continue; // an edited occurrence lives inside its master's resource
            }
            var calendarId = entry.Property(e => e.CalendarId);
            var left = entry.State == EntityState.Deleted
                || (entry.State == EntityState.Modified && calendarId.IsModified && calendarId.OriginalValue != calendarId.CurrentValue);
            if (left && !deletedCalendars.Contains(calendarId.OriginalValue))
            {
                gone.Add(new DeletedResource
                {
                    Id = Guid.NewGuid(),
                    CalendarId = calendarId.OriginalValue,
                    Uid = entry.Property(e => e.Uid).OriginalValue,
                    DeletedAtUtc = now,
                });
            }
        }
        foreach (var entry in ChangeTracker.Entries<TaskItem>())
        {
            var calendarId = entry.Property(t => t.CalendarId);
            var left = entry.State == EntityState.Deleted
                || (entry.State == EntityState.Modified && calendarId.IsModified && calendarId.OriginalValue != calendarId.CurrentValue);
            if (left && !deletedCalendars.Contains(calendarId.OriginalValue))
            {
                gone.Add(new DeletedResource
                {
                    Id = Guid.NewGuid(),
                    CalendarId = calendarId.OriginalValue,
                    Uid = entry.Property(t => t.Uid).OriginalValue,
                    DeletedAtUtc = now,
                });
            }
        }
        DeletedResources.AddRange(gone);
    }
}
