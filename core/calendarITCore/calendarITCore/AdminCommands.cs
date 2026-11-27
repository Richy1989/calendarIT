using CalendarIT.Infrastructure;
using CalendarIT.Infrastructure.Auth;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace calendarITCore;

/// <summary>
/// Commands an operator runs next to the server (<c>dotnet calendarITCore.dll &lt;command&gt;</c>)
/// instead of starting it. Their output goes to the operator's terminal only — never to the app log.
/// </summary>
public static class AdminCommands
{
    public const string ResetPasswordLink = "reset-password-link";

    /// <summary>Activates an account (confirms its address) — for servers without a system mail
    /// account, or a user whose activation mail doesn't arrive. The admin can do the same in
    /// Settings → Users.</summary>
    public const string ConfirmEmail = "confirm-email";

    /// <summary>The same as <see cref="ConfirmEmail"/>, by the name the web app uses.</summary>
    public const string ActivateAccount = "activate-account";

    /// <summary>Makes an account the server's admin (who activates new accounts) — and activates it.</summary>
    public const string MakeAdmin = "make-admin";

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && args[0] is ResetPasswordLink or ConfirmEmail or ActivateAccount or MakeAdmin;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            await error.WriteLineAsync($"Usage: dotnet calendarITCore.dll {args[0]} <email>");
            return 2;
        }

        var builder = WebApplication.CreateBuilder(args[2..]);
        // Nothing of this run belongs in the server's log (and the link must not reach any log).
        builder.Logging.ClearProviders();
        // No command signs a token, but the services insist on a key. In the published image the
        // key lives in /data and only the server process is handed it (entrypoint.sh), so a
        // `docker exec` of a command has none — and used to stop right here.
        if (string.IsNullOrWhiteSpace(builder.Configuration["JWT_SIGNING_KEY"]))
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JWT_SIGNING_KEY"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)),
            });
        }
        builder.Services.AddInfrastructure(builder.Configuration);
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await users.FindByEmailAsync(args[1].Trim());
        if (user?.Email is null)
        {
            await error.WriteLineAsync("No account has that email address.");
            return 1;
        }

        if (args[0] == MakeAdmin)
        {
            user.EmailConfirmed = true;
            await users.UpdateAsync(user);
            await AdminRole.GrantAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), user.Id);
            await output.WriteLineAsync($"{user.Email} is active and an admin: it can activate new accounts in Settings → Users.");
            return 0;
        }

        if (args[0] is ConfirmEmail or ActivateAccount)
        {
            if (!user.EmailConfirmed)
            {
                user.EmailConfirmed = true;
                var updated = await users.UpdateAsync(user);
                if (!updated.Succeeded)
                {
                    await error.WriteLineAsync(string.Join(" ", updated.Errors.Select(e => e.Description)));
                    return 1;
                }
            }
            await output.WriteLineAsync($"{user.Email} is activated: it can sign in now.");
            return 0;
        }

        var configured = app.Configuration["PUBLIC_BASE_URL"];
        var linkBase = string.IsNullOrWhiteSpace(configured) ? null : configured.TrimEnd('/');
        var link = await PasswordResetLinks.CreateAsync(users, user, linkBase);
        await output.WriteLineAsync(link);
        await error.WriteLineAsync(linkBase is null
            ? "PUBLIC_BASE_URL is not set: put the server's address in front of the path above."
            : "Single-use; it stops working once the password changes. Hand it over privately.");
        return 0;
    }
}
