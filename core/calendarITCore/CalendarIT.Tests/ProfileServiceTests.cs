using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using CalendarIT.Infrastructure.Profile;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

/// <summary>
/// The display preferences stored on the user row. The week start matters twice over: it decides
/// which column a calendar grid opens on, and "not chosen yet" has to stay distinguishable from
/// "chosen Sunday" — the client resolves the first case from the browser locale.
/// </summary>
public sealed class ProfileServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ProfileService _profile;
    private readonly Guid _userId = Guid.NewGuid();

    public ProfileServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser
        {
            Id = _userId, UserName = "owner@test.com", NormalizedUserName = "OWNER@TEST.COM",
            Email = "owner@test.com", NormalizedEmail = "OWNER@TEST.COM",
        });
        _db.SaveChanges();
        _profile = new ProfileService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task WeekStart_IsUnsetForANewAccount()
    {
        // Null is a real answer, not a missing one: it's what tells the client to follow the
        // browser locale rather than impose a week start on everybody.
        var dto = await _profile.GetAsync(_userId);
        Assert.Null(dto!.WeekStart);
    }

    [Theory]
    [InlineData("sunday")]
    [InlineData("monday")]
    public async Task WeekStart_AcceptsAKnownDay(string day)
    {
        Assert.True(await _profile.SetWeekStartAsync(_userId, day));

        var dto = await _profile.GetAsync(_userId);
        Assert.Equal(day, dto!.WeekStart);
    }

    [Fact]
    public async Task WeekStart_IsStoredLowercaseWhateverTheCasing()
    {
        // The client compares against "sunday"/"monday", so the stored value can't depend on how
        // the caller happened to capitalise it.
        Assert.True(await _profile.SetWeekStartAsync(_userId, "Monday"));

        var dto = await _profile.GetAsync(_userId);
        Assert.Equal("monday", dto!.WeekStart);
    }

    [Theory]
    [InlineData("tuesday")]
    [InlineData("")]
    [InlineData("1")]
    public async Task WeekStart_RejectsAnythingElseWithoutWriting(string day)
    {
        await _profile.SetWeekStartAsync(_userId, "monday");

        Assert.False(await _profile.SetWeekStartAsync(_userId, day));

        // The previous choice survives a bad request rather than being cleared by it.
        var dto = await _profile.GetAsync(_userId);
        Assert.Equal("monday", dto!.WeekStart);
    }

    [Fact]
    public async Task WeekStart_CanBeReturnedToAutomatic()
    {
        await _profile.SetWeekStartAsync(_userId, "monday");

        Assert.True(await _profile.SetWeekStartAsync(_userId, null));

        // Back to following the locale — the setting has three states, not two.
        var dto = await _profile.GetAsync(_userId);
        Assert.Null(dto!.WeekStart);
    }
}
