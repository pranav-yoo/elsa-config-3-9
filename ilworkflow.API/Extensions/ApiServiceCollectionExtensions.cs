using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlWorkflow.API.Extensions;

public static class ApiServiceCollectionExtensions
{
    /// <summary>Registers what only the HTTP layer needs: controllers, JSON format and the authorization policy.</summary>
    public static IServiceCollection AddApi(this IServiceCollection services)
    {
        services.AddControllers(options =>
        {
            // FluentValidation handles required fields, so non-nullable properties are not implicitly [Required].
            options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
        }).AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
            // /api/* request and response properties are snake_case (start_stage, stage_key, ...).
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        });

        services.AddAuthorization(options => options.AddPolicy("WorkflowConfigurator", policy =>
            policy.RequireAuthenticatedUser().RequireAssertion(context => PermissionChecks.IsConfigurator(context.User))));
        return services;
    }
}
