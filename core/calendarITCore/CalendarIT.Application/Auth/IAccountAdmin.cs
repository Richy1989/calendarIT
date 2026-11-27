namespace CalendarIT.Application.Auth;

/// <summary>One account as the admin's user list shows it.
/// <paramref name="Active"/>: activated (its address proven); <paramref name="IsYou"/>: the admin
/// asking, whose own account can't be disabled, deleted or demoted.</summary>
public sealed record UserAccountDto(
    Guid Id, string Email, DateTimeOffset? CreatedAt, DateTimeOffset? LastActiveAt,
    bool Active, bool Disabled, bool IsAdmin, bool IsYou);

public enum AccountAdminResult
{
    Done,

    /// <summary>The caller isn't the server's admin.</summary>
    Forbidden,

    /// <summary>No such account.</summary>
    NotFound,

    /// <summary>Not allowed for this account; <see cref="AccountAdminOutcome.Message"/> says why.</summary>
    Refused,
}

public sealed record AccountAdminOutcome(AccountAdminResult Result, string? Message = null, string? Link = null)
{
    public static readonly AccountAdminOutcome Done = new(AccountAdminResult.Done);
    public static readonly AccountAdminOutcome Forbidden = new(AccountAdminResult.Forbidden);
    public static readonly AccountAdminOutcome NotFound = new(AccountAdminResult.NotFound);

    public static AccountAdminOutcome Refused(string message) => new(AccountAdminResult.Refused, message);
}

/// <summary>
/// The server admin's account management. Every call checks the caller is an active, enabled
/// admin. The admin's own account can't be disabled, deleted or stripped of the role — which also
/// means the server always keeps at least one admin.
/// </summary>
public interface IAccountAdmin
{
    Task<bool> IsAdminAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Every account: waiting ones first (oldest first), then by email. Null when the caller isn't the admin.</summary>
    Task<IReadOnlyList<UserAccountDto>?> ListUsersAsync(Guid adminId, CancellationToken cancellationToken = default);

    /// <summary>Activates an account waiting for activation.</summary>
    Task<AccountAdminOutcome> ActivateAsync(Guid adminId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Disabling signs the account out everywhere at once and stops it signing in; its data stays.</summary>
    Task<AccountAdminOutcome> SetDisabledAsync(Guid adminId, Guid userId, bool disabled, CancellationToken cancellationToken = default);

    /// <summary>Deletes the account and everything in it.</summary>
    Task<AccountAdminOutcome> DeleteAsync(Guid adminId, Guid userId, CancellationToken cancellationToken = default);

    Task<AccountAdminOutcome> SetAdminAsync(Guid adminId, Guid userId, bool admin, CancellationToken cancellationToken = default);

    /// <summary>A single-use password-reset link for the account, in <see cref="AccountAdminOutcome.Link"/>
    /// — for the admin to hand over privately when the reset email can't reach the user. Relative
    /// (from the path) when PUBLIC_BASE_URL isn't set.</summary>
    Task<AccountAdminOutcome> CreateResetLinkAsync(Guid adminId, Guid userId, CancellationToken cancellationToken = default);
}
