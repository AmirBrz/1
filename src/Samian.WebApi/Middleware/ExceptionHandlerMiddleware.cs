using Microsoft.AspNetCore.Mvc;
using Samian.Application.Common;
using Samian.Domain.Exceptions;

namespace Samian.WebApi.Middleware;

// One place converts failures into HTTP, so no controller repeats a try/catch and no exception type can
// escape as an unhandled 500 with a stack trace.
//
// The mapping is the design (see Application/Common/Exceptions.cs for the same table):
//   400  the request was malformed, or an aggregate refused to exist (DomainException)
//   401  authentication failed (one message for both "no such user" and "wrong password")
//   403  authenticated, wrong role/ownership
//   404  the id does not exist
//   409  the document state or the stock says no (InvalidOrderStateException, InsufficientStockException)
//   500  everything else — including a DbUpdateException, which at this point IS a bug, not a rule
//
// Problem Details (RFC 9457) is the shape ASP.NET Core already produces for model-binding failures, so
// hand-written errors and framework errors look identical to the client.
public sealed class ExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlerMiddleware> _logger;

    public ExceptionHandlerMiddleware(RequestDelegate next, ILogger<ExceptionHandlerMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            // HasStarted guard: once headers are on the wire, the only honest action is to rethrow —
            // writing a 500 body into a stream that already promised 200 corrupts the response.
            await WriteProblemAsync(context, exception);
        }
    }

    private async Task WriteProblemAsync(HttpContext context, Exception exception)
    {
        var (status, title) = MapToHttp(exception);

        if (status >= 500)
            _logger.LogError(exception, "Unhandled exception while executing {Method} {Path}.", context.Request.Method, context.Request.Path);
        else
            _logger.LogWarning("{Status} while executing {Method} {Path}: {Message}", status, context.Request.Method, context.Request.Path, exception.Message);

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        var problem = exception is AppValidationException validation
            ? new ValidationProblemDetails
            {
                Title = title,
                Status = status,
                Detail = validation.Message,
                Instance = context.Request.Path,
            }
            : new ProblemDetails
            {
                Title = title,
                Status = status,
                Detail = exception.Message,
                Instance = context.Request.Path,
            };

        // The validation variant carries the per-field messages the request validators collected, which
        // is the difference between "something is wrong" and "the currency must be three uppercase
        // letters" for whoever is integrating against this API.
        if (exception is AppValidationException errors)
            problem.Extensions["errors"] = errors.Errors;

        problem.Extensions["traceId"] = context.TraceIdentifier;

        await context.Response.WriteAsJsonAsync(problem);
    }

    // Order matters: InvalidOrderStateException and InsufficientStockException ARE DomainExceptions, so
    // the specific types must be tested before the general one or every business refusal becomes a 400.
    private static (int Status, string Title) MapToHttp(Exception exception) => exception switch
    {
        AppValidationException => (StatusCodes.Status400BadRequest, "Request validation failed"),
        InvalidOrderStateException => (StatusCodes.Status409Conflict, "Order is not in a state that allows this action"),
        InsufficientStockException => (StatusCodes.Status409Conflict, "Not enough stock"),
        DomainException => (StatusCodes.Status400BadRequest, "Business rule refused the change"),
        InvalidCredentialsException => (StatusCodes.Status401Unauthorized, "Authentication failed"),
        ForbiddenActionException => (StatusCodes.Status403Forbidden, "Not allowed"),
        ResourceNotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
        BusinessConflictException => (StatusCodes.Status409Conflict, "Conflict"),
        // Not a switch arm on purpose: an unexpected exception must stay 500 so it is loud in the logs.
        _ => (StatusCodes.Status500InternalServerError, "Unexpected server error"),
    };
}

public static class ExceptionHandlerMiddlewareExtensions
{
    public static IApplicationBuilder UseSamianErrorHandling(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<ExceptionHandlerMiddleware>();
    }
}
