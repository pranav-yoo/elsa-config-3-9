using IlWorkflow.API.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IlWorkflow.API.Middleware;

/// <summary>Elsa's native /elsa/api/* endpoints keep Elsa's own format; everything under /api uses <c>Result&lt;T&gt;</c>.</summary>
public static class ApiResponse
{
    public static bool Applies(HttpContext http) => http.Request.Path.StartsWithSegments("/api");

    /// <summary>Error body: Result&lt;T&gt; for /api/*, otherwise the previous { code, message, errors } shape.</summary>
    public static Task WriteErrorAsync(HttpContext http, int status, string code, string message, object? errors = null)
    {
        http.Response.StatusCode = status;
        if (Applies(http)) return ApiResultHelper.WriteAsync(http, ApiResultHelper.Error(status, code, message, errors));
        return errors is null ? http.Response.WriteAsJsonAsync(new { code, message }) : http.Response.WriteAsJsonAsync(new { code, errors });
    }
}

/// <summary>Model-binding failures (malformed body, wrong types) never reach a controller action; give them the same Result shape.</summary>
public sealed class ApiResponseFilter : IAsyncResultFilter
{
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: ValidationProblemDetails problem } && ApiResponse.Applies(context.HttpContext))
        {
            var errors = problem.Errors.SelectMany(e => e.Value.Select(m => new { field = e.Key, message = m })).ToArray();
            context.Result = ApiResultHelper.Error(StatusCodes.Status400BadRequest, "invalid_request", string.Join(" ", errors.Select(e => e.message)), errors);
        }
        return next();
    }
}
