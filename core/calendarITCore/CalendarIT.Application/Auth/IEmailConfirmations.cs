namespace CalendarIT.Application.Auth;

/// <summary>
/// Proof that an account's owner can read its email address — what activates a new account
/// (anyone can sign up with any address; an account signs in only once it is activated).
/// </summary>
public interface IEmailConfirmations
{
    /// <summary>Confirmation links can be mailed (system mail account + PUBLIC_BASE_URL).</summary>
    bool Available { get; }

    /// <summary>Mails a fresh confirmation link to the user, unless already confirmed, unavailable
    /// or one went out moments ago. Returns whether one was queued.</summary>
    Task<bool> SendAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary><see cref="SendAsync"/> for the account with this address, if there is one
    /// (for the sign-in screen, where nobody is signed in).</summary>
    Task SendToAddressAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Confirms the address the link was sent to; false for an unknown or stale link.</summary>
    Task<bool> ConfirmAsync(ConfirmEmailRequest request, CancellationToken cancellationToken = default);
}
