using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IlWorkflow.API.Middleware;

/// <summary>
/// Turns known exceptions into clean HTTP errors, so code anywhere can simply <c>throw</c>.
/// Under /api/* the body is a <see cref="Result{T}"/>; under /elsa/api/* it is { code, message }.
/// Anything not listed here becomes a 500 with a trace ID (see Program.cs).
/// </summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        try
        {
            await next(http);
        }
        catch (ValidationException exception)
        {
            var errors = exception.Errors.Select(error => new { field = error.PropertyName, message = error.ErrorMessage }).ToArray();
            await ApiResultHelper.WriteErrorAsync(http, 400, "validation_failed", string.Join(" ", errors.Select(error => error.message)), errors);
        }
        catch (ApiException exception)
        {
            await ApiResultHelper.WriteErrorAsync(http, exception.Status, "request_rejected", exception.Message);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two identical task commands raced; the database unique index caught the second one.
            await ApiResultHelper.WriteErrorAsync(http, 409, "operation_conflict", "A competing operation was already recorded. Reload the task before retrying.");
        }
        catch (DbUpdateConcurrencyException)
        {
            await ApiResultHelper.WriteErrorAsync(http, 409, "revision_conflict", "The record changed. Read the current revision and retry.");
        }
        catch (BadHttpRequestException exception)
        {
            await ApiResultHelper.WriteErrorAsync(http, exception.StatusCode, "invalid_request", "Check request size and content type.");
        }
        catch (JsonException)
        {
            await ApiResultHelper.WriteErrorAsync(http, 400, "invalid_json", "Check JSON types, enum names and required objects.");
        }
    }
}
