using System.Diagnostics;

namespace IlWorkflow.API.Middleware;

/// <summary>Adds the X-Trace-Id header, a log scope and one log line per request; 401/403 get a hint on what to check.</summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        using var scope = logger.BeginScope("TraceId={TraceId}", context.TraceIdentifier);
        context.Response.Headers["X-Trace-Id"] = context.TraceIdentifier;
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            logger.LogError(exception, "Unhandled error for {Method} {Path}", context.Request.Method, context.Request.Path);
            throw;
        }
        finally
        {
            logger.LogInformation("{Method} {Path} => {StatusCode} in {ElapsedMs:F1} ms",
                context.Request.Method, context.Request.Path, context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
                logger.LogWarning("401: Authorization header present={HeaderPresent}. POST /elsa/api/identity/login, then paste accessToken into Swagger Authorize (JWT bearer field). Tokens from a different signing key or expired tokens must be replaced.",
                    context.Request.Headers.ContainsKey("Authorization"));
            if (context.Response.StatusCode == StatusCodes.Status403Forbidden)
                logger.LogWarning("403: Request denied by authorization. Authenticated={Authenticated}; check the account permissions.", context.User.Identity?.IsAuthenticated == true);
        }
    }
}
