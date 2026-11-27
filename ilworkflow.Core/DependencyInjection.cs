using Microsoft.Extensions.DependencyInjection;

namespace IlWorkflow.Core;

public static class DependencyInjection
{
    /// <summary>Registers the business rules of Core. Infrastructure supplies the interfaces they depend on (for example IApproverDirectory).</summary>
    public static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.AddScoped<WorkflowConfigurationValidator>();
        services.AddScoped<AssignmentResolver>();
        return services;
    }
}
