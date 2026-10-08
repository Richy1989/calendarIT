using CalendarIT.Application;
using Microsoft.AspNetCore.Diagnostics;

namespace calendarITCore.Extensions;

/// <summary>
/// Turns <see cref="InvalidInputException"/> — input the caller can fix, like a repeat rule that
/// doesn't parse or a file that isn't iCalendar — into a 400 with its message. Anything else that
/// escapes a request becomes a bare 500 problem response from the framework: never a stack trace,
/// and never a half-written body.
/// </summary>
public sealed class InvalidInputExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not InvalidInputException invalid)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "The request couldn't be processed.",
                Detail = invalid.Message,
            },
        });
    }
}
