using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CalendarIT.CalDav;

/// <summary>Registers the CalDAV Basic-auth scheme and maps the /dav endpoint tree.</summary>
public static class CalDavEndpoints
{
    public static IServiceCollection AddCalDav(this IServiceCollection services)
    {
        // Registered alongside the default JWT scheme; only the /dav endpoints ask for it.
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, CalDavBasicAuthHandler>(CalDavBasicAuthHandler.SchemeName, null);
        services.AddScoped<CalDavHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapCalDav(this IEndpointRouteBuilder app)
    {
        // RFC 6764 bootstrapping: clients probe the domain root for this path.
        app.MapMethods("/.well-known/caldav", ["GET", "OPTIONS", "PROPFIND"],
            () => Results.Redirect("/dav/", permanent: true)).AllowAnonymous();

        var dav = app.MapGroup("/dav")
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = CalDavBasicAuthHandler.SchemeName });

        dav.MapMethods("/", ["OPTIONS"], (CalDavHandler h, HttpContext ctx) => h.Options(ctx));
        dav.MapMethods("/{**path}", ["OPTIONS"], (CalDavHandler h, HttpContext ctx) => h.Options(ctx));

        dav.MapMethods("/", ["PROPFIND"], (CalDavHandler h, HttpContext ctx) => h.PropfindRoot(ctx));
        dav.MapMethods("/principal", ["PROPFIND"], (CalDavHandler h, HttpContext ctx) => h.PropfindPrincipal(ctx));
        dav.MapMethods("/calendars", ["PROPFIND"], (CalDavHandler h, HttpContext ctx) => h.PropfindHome(ctx));
        dav.MapMethods("/calendars/{calendarId:guid}", ["PROPFIND"],
            (Guid calendarId, CalDavHandler h, HttpContext ctx) => h.PropfindCalendar(calendarId, ctx));
        dav.MapMethods("/calendars/{calendarId:guid}", ["REPORT"],
            (Guid calendarId, CalDavHandler h, HttpContext ctx) => h.Report(calendarId, ctx));

        dav.MapMethods("/calendars/{calendarId:guid}/{resource}", ["PROPFIND"],
            (Guid calendarId, string resource, CalDavHandler h, HttpContext ctx) => h.PropfindEvent(calendarId, resource, ctx));
        dav.MapMethods("/calendars/{calendarId:guid}/{resource}", ["GET", "HEAD"],
            (Guid calendarId, string resource, CalDavHandler h, HttpContext ctx) => h.GetEvent(calendarId, resource, ctx));
        dav.MapMethods("/calendars/{calendarId:guid}/{resource}", ["PUT"],
            (Guid calendarId, string resource, CalDavHandler h, HttpContext ctx) => h.PutEvent(calendarId, resource, ctx));
        dav.MapMethods("/calendars/{calendarId:guid}/{resource}", ["DELETE"],
            (Guid calendarId, string resource, CalDavHandler h, HttpContext ctx) => h.DeleteEvent(calendarId, resource, ctx));

        return app;
    }
}
