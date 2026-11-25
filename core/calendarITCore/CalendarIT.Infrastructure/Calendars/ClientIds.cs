using CalendarIT.Application;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// Ids a client chose for a row it creates (the app works offline, so it names things before the
/// server has seen them). An id is a key across all users, so one already taken — by anyone — is
/// refused without saying whose it is.
/// </summary>
public static class ClientIds
{
    public const string InUseMessage = "That id is already in use.";

    /// <summary>The requested id when it is free, a new one when none was asked for; throws
    /// <see cref="InvalidInputException"/> when it is taken or empty.</summary>
    public static async Task<Guid> ResolveAsync<T>(
        IQueryable<T> table, Func<Guid, System.Linq.Expressions.Expression<Func<T, bool>>> byId, Guid? requested,
        CancellationToken cancellationToken)
    {
        if (requested is not { } id)
        {
            return Guid.NewGuid();
        }
        if (id == Guid.Empty || await table.AnyAsync(byId(id), cancellationToken))
        {
            throw new InvalidInputException(InUseMessage);
        }
        return id;
    }
}
