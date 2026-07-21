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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

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

            entity.HasIndex(e => e.CalendarId);
            entity.HasIndex(e => new { e.CalendarId, e.StartUtc });

            entity.HasOne(e => e.Calendar)
                .WithMany(c => c.Events)
                .HasForeignKey(e => e.CalendarId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
