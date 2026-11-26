using System.Buffers.Text;
using System.Text;
using CalendarIT.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace CalendarIT.Infrastructure.Auth;

/// <summary>
/// Builds a single-use password-reset link. Used by "Forgot your password?" (which mails it) and by
/// the operator's <c>reset-password-link</c> command (which prints it to the operator's terminal).
/// A usable link is never written to the application log: logs are shipped, kept and shared far
/// more widely than the database.
/// </summary>
public static class PasswordResetLinks
{
    public const string Path = "/reset-password";

    public static async Task<string> CreateAsync(UserManager<ApplicationUser> userManager, ApplicationUser user, string? linkBase)
    {
        // Base64Url because the raw token contains characters that don't survive a query string.
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var encoded = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(token));
        return $"{linkBase?.TrimEnd('/')}{Path}?email={Uri.EscapeDataString(user.Email ?? "")}&token={encoded}";
    }
}
