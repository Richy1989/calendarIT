using CalendarIT.Infrastructure;
using CalendarIT.Infrastructure.Auth;
using CalendarIT.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace calendarITCore;

/// <summary>
/// Commands an operator runs next to the server (<c>dotnet calendarITCore.dll &lt;command&gt;</c>)
/// instead of starting it. Their output goes to the operator's terminal only — never to the app log.
/// </summary>
public static class AdminCommands
{
    public const string ResetPasswordLink = "reset-password-link";

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == ResetPasswordLink;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            await error.WriteLineAsync($"Usage: dotnet calendarITCore.dll {ResetPasswordLink} <email>");
            return 2;
        }

        var builder = WebApplication.CreateBuilder(args[2..]);
        // Nothing of this run belongs in the server's log (and the link must not reach any log).
        builder.Logging.ClearProviders();
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
