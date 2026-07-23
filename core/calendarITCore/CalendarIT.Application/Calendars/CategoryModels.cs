using System.ComponentModel.DataAnnotations;

namespace CalendarIT.Application.Calendars;

/// <summary>A user-defined event category (named color), as returned to the client.</summary>
public sealed record CategoryDto(Guid Id, string Name, string Color, int EventCount);

/// <summary>Create/update payload for a category.</summary>
public sealed class SaveCategoryRequest
{
    [Required, MaxLength(100)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Hex color (e.g. "#7B68EE").</summary>
    [Required, RegularExpression("^#[0-9a-fA-F]{6}$")]
    public string Color { get; init; } = string.Empty;
}

/// <summary>Outcome of a category create/update — names are unique per user.</summary>
public enum CategorySaveStatus
{
    Saved,
    NotFound,
    DuplicateName,
}

/// <summary>Result of a category save; <see cref="Category"/> is set when <see cref="Status"/> is Saved.</summary>
public sealed record CategorySaveOutcome(CategorySaveStatus Status, CategoryDto? Category);

/// <summary>CRUD over the user's categories (events take their display color from them).</summary>
public interface ICategoryService
{
    Task<IReadOnlyList<CategoryDto>> ListAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<CategorySaveOutcome> CreateAsync(Guid userId, SaveCategoryRequest request, CancellationToken cancellationToken = default);

    Task<CategorySaveOutcome> UpdateAsync(Guid userId, Guid categoryId, SaveCategoryRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a category; its events become uncategorized (default color).</summary>
    Task<bool> DeleteAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken = default);
}
