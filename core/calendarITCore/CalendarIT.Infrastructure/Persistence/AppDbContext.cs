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

    public DbSet<Reminder> Reminders => Set<Reminder>();

    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();

    public DbSet<MailAccount> MailAccounts => Set<MailAccount>();

    public DbSet<Attendee> Attendees => Set<Attendee>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(u => u.AvatarContentType).HasMaxLength(100);
        });

        builder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(t => t.Id);

            entity.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(t => t.TokenHash).IsUnique();

            entity.Property(t => t.ReplacedByTokenHash).HasMaxLength(128);

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
            entity.HasIndex(c => c.OwnerUserId);

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

            entity.HasIndex(e => e.CalendarId);
            entity.HasIndex(e => new { e.CalendarId, e.StartUtc });

            entity.HasOne(e => e.Calendar)
                .WithMany(c => c.Events)
                .HasForeignKey(e => e.CalendarId)
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

        builder.Entity<MailAccount>(entity =>
        {
            // One account per user: the user id doubles as the key.
            entity.HasKey(a => a.UserId);
            entity.Property(a => a.Address).HasMaxLength(320).IsRequired();
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
}
