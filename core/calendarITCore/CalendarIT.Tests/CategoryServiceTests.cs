using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalendarIT.Tests;

public sealed class CategoryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly CategoryService _service;
    private readonly EventService _events;
    private readonly Guid _userId = Guid.NewGuid();

    public CategoryServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "cat@test", NormalizedUserName = "CAT@TEST" });
        _db.SaveChanges();
        _service = new CategoryService(_db, TimeProvider.System);
        _events = new EventService(_db, TimeProvider.System, new FakeInvitationMailer());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CreateUpdateDelete_RoundTrip()
    {
        var created = await _service.CreateAsync(_userId, new SaveCategoryRequest { Name = "Work", Color = "#6495ED" });
        Assert.Equal(CategorySaveStatus.Saved, created.Status);

        var updated = await _service.UpdateAsync(
            _userId, created.Category!.Id, new SaveCategoryRequest { Name = "Office", Color = "#FF6347" });
        Assert.Equal(CategorySaveStatus.Saved, updated.Status);
        Assert.Equal("Office", updated.Category!.Name);
        Assert.Equal("#FF6347", updated.Category.Color);

        Assert.True(await _service.DeleteAsync(_userId, created.Category.Id));
        Assert.Empty(await _service.ListAsync(_userId));
    }

    [Fact]
    public async Task Create_DuplicateName_IsRefused_CaseInsensitively()
    {
        await _service.CreateAsync(_userId, new SaveCategoryRequest { Name = "Work", Color = "#6495ED" });
        var dup = await _service.CreateAsync(_userId, new SaveCategoryRequest { Name = "  work ", Color = "#000000" });
        Assert.Equal(CategorySaveStatus.DuplicateName, dup.Status);

        // Renaming a category to its own name is fine (the check excludes itself).
        var only = Assert.Single(await _service.ListAsync(_userId));
        var self = await _service.UpdateAsync(_userId, only.Id, new SaveCategoryRequest { Name = "Work", Color = "#111111" });
        Assert.Equal(CategorySaveStatus.Saved, self.Status);
    }

    [Fact]
    public async Task OtherUsersCategories_AreInvisibleAndUntouchable()
    {
        var otherUser = Guid.NewGuid();
        _db.Users.Add(new ApplicationUser { Id = otherUser, UserName = "b@test", NormalizedUserName = "B@TEST" });
        await _db.SaveChangesAsync();
        var theirs = await _service.CreateAsync(otherUser, new SaveCategoryRequest { Name = "Theirs", Color = "#123456" });

        Assert.Empty(await _service.ListAsync(_userId));
        Assert.Equal(CategorySaveStatus.NotFound, (await _service.UpdateAsync(
            _userId, theirs.Category!.Id, new SaveCategoryRequest { Name = "Mine", Color = "#000000" })).Status);
        Assert.False(await _service.DeleteAsync(_userId, theirs.Category.Id));
    }

    [Fact]
    public async Task EventTakesItsColorFromTheCategory_AndSurvivesCategoryDelete()
    {
        var cat = (await _service.CreateAsync(_userId, new SaveCategoryRequest { Name = "Work", Color = "#6495ED" })).Category!;

        var e = await _events.CreateAsync(_userId, new SaveEventRequest
        {
            Title = "Standup",
            Start = DateTimeOffset.UtcNow,
            CategoryId = cat.Id,
        });
        Assert.Equal(cat.Id, e.CategoryId);
        Assert.Equal("#6495ED", e.Color);

        // Recoloring the category recolors the event on the next read.
        await _service.UpdateAsync(_userId, cat.Id, new SaveCategoryRequest { Name = "Work", Color = "#FF6347" });
        var reread = await _events.GetByIdAsync(_userId, e.Id);
        Assert.Equal("#FF6347", reread!.Color);

        // Deleting the category leaves the event uncategorized, not deleted.
        await _service.DeleteAsync(_userId, cat.Id);
        reread = await _events.GetByIdAsync(_userId, e.Id);
        Assert.NotNull(reread);
        Assert.Null(reread.CategoryId);
        Assert.Null(reread.Color);
    }

    [Fact]
    public async Task Event_WithForeignCategory_KeepsItUnassigned()
    {
        var otherUser = Guid.NewGuid();
        _db.Users.Add(new ApplicationUser { Id = otherUser, UserName = "c@test", NormalizedUserName = "C@TEST" });
        await _db.SaveChangesAsync();
        var theirs = (await _service.CreateAsync(otherUser, new SaveCategoryRequest { Name = "Theirs", Color = "#123456" })).Category!;

        var e = await _events.CreateAsync(_userId, new SaveEventRequest
        {
            Title = "Sneaky",
            Start = DateTimeOffset.UtcNow,
            CategoryId = theirs.Id,
        });
        Assert.Null(e.CategoryId);
    }

    [Fact]
    public async Task Backfill_TurnsEventColorsIntoCategories()
    {
        // Two events sharing a color, one distinct — as left behind by the pre-category schema.
        var now = DateTime.UtcNow;
        var cal = new Calendar { Id = Guid.NewGuid(), OwnerUserId = _userId, Name = "Personal", CreatedAt = now, UpdatedAt = now };
        _db.Calendars.Add(cal);
        _db.Events.AddRange(
            new CalendarEvent { Id = Guid.NewGuid(), CalendarId = cal.Id, Uid = "a@t", Title = "A", Color = "#7B68EE", StartUtc = now, CreatedAt = now, UpdatedAt = now },
            new CalendarEvent { Id = Guid.NewGuid(), CalendarId = cal.Id, Uid = "b@t", Title = "B", Color = "#7B68EE", StartUtc = now, CreatedAt = now, UpdatedAt = now },
            new CalendarEvent { Id = Guid.NewGuid(), CalendarId = cal.Id, Uid = "c@t", Title = "C", Color = "#40E0D0", StartUtc = now, CreatedAt = now, UpdatedAt = now },
            new CalendarEvent { Id = Guid.NewGuid(), CalendarId = cal.Id, Uid = "d@t", Title = "D", Color = null, StartUtc = now, CreatedAt = now, UpdatedAt = now });
        await _db.SaveChangesAsync();

        var services = new ServiceCollectionForBackfill(_db);
        await services.Provider.BackfillCategoriesAsync();

        var categories = await _db.Categories.Where(c => c.OwnerUserId == _userId).OrderBy(c => c.Name).ToListAsync();
        Assert.Equal(2, categories.Count);
        Assert.Equal("Mediumslateblue", categories[0].Name);
        Assert.Equal("#7B68EE", categories[0].Color);
        Assert.Equal("Turquoise", categories[1].Name);

        Assert.Equal(2, await _db.Events.CountAsync(e => e.CategoryId == categories[0].Id));
        Assert.Equal(1, await _db.Events.CountAsync(e => e.CategoryId == categories[1].Id));
        Assert.Null((await _db.Events.SingleAsync(e => e.Uid == "d@t")).CategoryId);

        // Idempotent: a second run creates nothing new.
        await services.Provider.BackfillCategoriesAsync();
        Assert.Equal(2, await _db.Categories.CountAsync(c => c.OwnerUserId == _userId));
    }
}

/// <summary>Minimal service provider so the startup backfill extension can run on the test db.</summary>
file sealed class ServiceCollectionForBackfill(AppDbContext db)
{
    public IServiceProvider Provider { get; } = new ServiceCollection()
        .AddLogging()
        .AddSingleton(TimeProvider.System)
        .AddSingleton(db)
        .BuildServiceProvider();
}
