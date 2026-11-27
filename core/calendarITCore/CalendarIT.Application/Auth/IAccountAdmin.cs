namespace CalendarIT.Application.Auth;

/// <summary>An account that signed up and waits for activation.</summary>
public sealed record PendingAccountDto(Guid Id, string Email, DateTimeOffset? CreatedAt);

public enum AccountAdminResult
{
    Done,

    /// <summary>The caller isn't the server's admin.</summary>
    Forbidden,

    /// <summary>No such account waiting for activation (unknown, or already active).</summary>
    NotFound,
}

/// <summary>What the server's admin does with new accounts. Every call checks the caller is
/// the admin; only accounts still waiting for activation can be seen or touched.</summary>
public interface IAccountAdmin
{
    Task<bool> IsAdminAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Accounts waiting for activation, oldest first; null when the caller isn't the admin.</summary>
    Task<IReadOnlyList<PendingAccountDto>?> ListPendingAsync(Guid adminId, CancellationToken cancellationToken = default);

    Task<AccountAdminResult> ActivateAsync(Guid adminId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Removes an account that was never activated (it has nothing in it but defaults).</summary>
    Task<AccountAdminResult> DeleteAsync(Guid adminId, Guid userId, CancellationToken cancellationToken = default);
}
