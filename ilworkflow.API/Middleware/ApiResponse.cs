using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IlWorkflow.API.Middleware;

/// <summary>
/// Company response format for this application's /api/* endpoints:
/// { "trace_id": "...", "messages": ["..."], "data": { ... } }.
/// Elsa's native /elsa/api/* endpoints keep Elsa's own format.
/// </summary>
public static class ApiResponse
{
    public static bool Applies(HttpContext http) => http.Request.Path.StartsWithSegments("/api");

    public static object Envelope(HttpContext http, object? data, IEnumerable<string>? messages = null) => new Dictionary<string, object?>
    {
        ["trace_id"] = http.TraceIdentifier,
        ["messages"] = (messages ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).ToArray(),
        ["data"] = data ?? new Dictionary<string, object?>()
    };

    /// <summary>Error body: the envelope for /api/*, otherwise the previous { code, message, errors } shape.</summary>
    public static Task WriteErrorAsync(HttpContext http, int status, string code, string message, object? errors = null)
    {
        http.Response.StatusCode = status;
        if (!Applies(http))
            return errors is null ? http.Response.WriteAsJsonAsync(new { code, message }) : http.Response.WriteAsJsonAsync(new { code, errors });
        var data = new Dictionary<string, object?> { ["code"] = code };
        if (errors is not null) data["errors"] = errors;
        return http.Response.WriteAsJsonAsync(Envelope(http, data, [message]));
    }
}

/// <summary>Wraps every controller result (200/202/400/409...) in the company envelope.</summary>
public sealed class ApiResponseFilter : IAsyncResultFilter
{
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult result && ApiResponse.Applies(context.HttpContext))
        {
            var messages = new List<string>();
            object? data = result.Value;
            if (result.Value is ValidationProblemDetails problem)
            {
                // Model binding failures (malformed body, wrong types).
                var errors = problem.Errors.SelectMany(e => e.Value.Select(m => new { field = e.Key, message = m })).ToArray();
                messages.AddRange(errors.Select(e => e.message));
                data = new { code = "invalid_request", errors };
            }
            else if (result.StatusCode == StatusCodes.Status202Accepted) messages.Add("Accepted. The next task may take a moment to appear.");
            else if (result.StatusCode == StatusCodes.Status409Conflict) messages.Add("Conflict. Reload the task and retry.");
            result.Value = ApiResponse.Envelope(context.HttpContext, data, messages);
            result.DeclaredType = null;
        }
        return next();
    }
}
