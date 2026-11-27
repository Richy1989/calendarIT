using System.Buffers.Text;
using System.Text;
using CalendarIT.Application.Auth;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace CalendarIT.Infrastructure.Auth;

/// <summary>
/// Email confirmation, mailed through the operator's system account only (see
/// <see cref="SystemMailOptions"/> for why never the user's own). The token is Identity's
/// Data-Protection-signed one: bound to the user's security stamp, two hours long.
/// </summary>
public sealed class EmailConfirmations(
    UserManager<ApplicationUser> userManager,
    IMailOutbox outbox,
    SystemMailOptions systemMail,
    IOptions<AuthOptions> authOptionsAccessor,
    ILogger<EmailConfirmations> logger,
    IMemoryCache? cache = null) : IEmailConfirmations
{
    public const string Path = "/confirm-email";

    private readonly AuthOptions authOptions = authOptionsAccessor.Value;

    /// <summary>One mail per account per this long: the resend button shouldn't fill an inbox.</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    public bool Available => systemMail.IsConfigured && !string.IsNullOrWhiteSpace(authOptions.PublicBaseUrl);

    public async Task<bool> SendAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!Available)
        {
            return false;
        }
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user?.Email is null || user.EmailConfirmed)
        {
            return false;
        }
        if (cache is not null)
        {
            var key = $"email-confirmation-sent:{user.Id:N}";
            if (cache.TryGetValue(key, out _))
            {
                return false;
            }
            cache.Set(key, true, Cooldown);
        }

        var token = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(await userManager.GenerateEmailConfirmationTokenAsync(user)));
        var link = $"{authOptions.PublicBaseUrl!.TrimEnd('/')}{Path}?email={Uri.EscapeDataString(user.Email)}&token={token}";
        var message = new MimeMessage();
        message.To.Add(MailboxAddress.Parse(user.Email));
        message.Subject = "Activate your 24seven account";
        message.Body = new TextPart("plain")
        {
            Text = $"""
                Someone signed up for 24seven with this email address.

                If it was you, open this link to activate your account:

                {link}

                The link works for two hours; the sign-in screen can send a new one. If this
                wasn't you, ignore this email — the account stays inactive and can't be used.
                """,
        };
        return await outbox.QueueSystemAsync(user.Id, message, OutboxKind.EmailConfirmation, cancellationToken);
    }

    public async Task SendToAddressAsync(string email, CancellationToken cancellationToken = default)
    {
        if (await userManager.FindByEmailAsync(email) is { } user)
        {
            await SendAsync(user.Id, cancellationToken);
        }
    }

    public async Task<bool> ConfirmAsync(ConfirmEmailRequest request, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            return false;
        }
        if (user.EmailConfirmed)
        {
            return true; // a second click on the same link
        }
        string token;
        try
        {
            token = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(request.Token));
        }
        catch (FormatException)
        {
            return false;
        }
        var result = await userManager.ConfirmEmailAsync(user, token);
        if (result.Succeeded)
        {
            logger.LogInformation("User {UserId} confirmed their email address", user.Id);
        }
        return result.Succeeded;
    }
}
