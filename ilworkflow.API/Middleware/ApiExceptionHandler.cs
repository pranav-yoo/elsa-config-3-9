namespace IlWorkflow.API.Middleware;

public static class ApiExceptionHandler
{
    /// <summary>Unhandled exceptions become a 500 with the trace ID; details stay in the server log.</summary>
    public static IApplicationBuilder UseApiExceptionHandler(this IApplicationBuilder app) =>
        app.UseExceptionHandler(errors => errors.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.Headers["X-Trace-Id"] = context.TraceIdentifier;
            const string message = "An unexpected error occurred. Check server logs using the trace ID.";
            if (ApiResultHelper.Applies(context))
                await ApiResultHelper.WriteAsync(context, ApiResultHelper.Error(StatusCodes.Status500InternalServerError, "internal_error", message));
            else
                await context.Response.WriteAsJsonAsync(new { Code = "internal_error", Message = message, TraceId = context.TraceIdentifier });
        }));
}
