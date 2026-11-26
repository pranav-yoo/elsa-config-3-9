using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace IlWorkflow.API.Extensions;

public static class AuthenticationExtensions
{
    /// <summary>Logs why a bearer token was rejected, with the trace ID, for every JWT scheme.</summary>
    public static IServiceCollection AddBearerFailureLogging(this IServiceCollection services) =>
        services.PostConfigureAll<JwtBearerOptions>(options =>
        {
            var previous = options.Events.OnAuthenticationFailed;
            options.Events.OnAuthenticationFailed = async context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Authentication");
                logger.LogWarning("Bearer authentication failed: {FailureType}. Sign in again and use the accessToken in Swagger Authorize. TraceId={TraceId}",
                    context.Exception.GetType().Name, context.HttpContext.TraceIdentifier);
                if (previous is not null) await previous(context);
            };
        });
}
