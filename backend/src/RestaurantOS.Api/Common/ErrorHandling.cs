using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RestaurantOS.Domain.Common;

namespace RestaurantOS.Api.Common;

public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, code, title) = exception switch
        {
            ConflictException c => (StatusCodes.Status409Conflict, c.Code, c.Message),
            DomainException d => (StatusCodes.Status400BadRequest, d.Code, d.Message),
            NotFoundException n => (StatusCodes.Status404NotFound, "not_found", n.Message),
            ForbiddenException f => (StatusCodes.Status403Forbidden, "forbidden", f.Message),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "unauthorized", "Authentication required."),
            SubscriptionInactiveException s => (StatusCodes.Status402PaymentRequired, "subscription_inactive", s.Message),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "concurrency_conflict",
                "This record was changed by someone else. Reload and try again."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                (StatusCodes.Status409Conflict, "duplicate", "A record with the same unique value already exists."),
            BadHttpRequestException b => (b.StatusCode, "bad_request", b.Message),
            _ => (StatusCodes.Status500InternalServerError, "server_error", "An unexpected error occurred."),
        };

        if (status >= 500) logger.LogError(exception, "Unhandled exception");

        http.Response.StatusCode = status;
        var details = new ProblemDetails { Status = status, Title = title, Type = $"https://restaurantos.dev/errors/{code}" };
        details.Extensions["code"] = code;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = details, Exception = exception });
    }
}
