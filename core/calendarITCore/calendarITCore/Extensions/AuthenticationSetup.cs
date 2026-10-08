using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using CalendarIT.Infrastructure.Auth;
using Microsoft.IdentityModel.Tokens;

namespace calendarITCore.Extensions;

/// <summary>
/// Configures JWT bearer authentication for the API. Validation parameters mirror the
/// token-issuing side (<c>CalendarIT.Infrastructure</c>): same signing key, issuer, and
/// audience, all sourced from the <c>JWT_*</c> environment variables.
/// </summary>
public static class AuthenticationSetup
{
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var signingKey = configuration["JWT_SIGNING_KEY"]
            ?? throw new InvalidOperationException("JWT_SIGNING_KEY must be set.");
        var issuer = configuration["JWT_ISSUER"] ?? "calendarit";
        var audience = configuration["JWT_AUDIENCE"] ?? "calendarit";

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    // Exactly the algorithm we sign with; nothing negotiable from the token header.
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                };
                options.Events = new JwtBearerEvents
                {
                    // A signature proves the token was issued, not that its session is still
                    // signed in. Tokens from a signed-out session stop here instead of living out
                    // their remaining minutes.
                    OnTokenValidated = async context =>
                    {
                        var sid = context.Principal?.GetSessionId();
                        if (sid is null)
                        {
                            return; // issued before sessions existed; expires within minutes
                        }
                        var sessions = context.HttpContext.RequestServices.GetRequiredService<SessionValidator>();
                        if (!await sessions.IsActiveAsync(sid.Value, context.HttpContext.RequestAborted))
                        {
                            context.Fail("This session has been signed out.");
                        }
                    },
                };
            });

        services.AddAuthorization();
        return services;
    }
}
