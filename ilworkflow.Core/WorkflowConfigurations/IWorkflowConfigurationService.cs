namespace IlWorkflow.Core.WorkflowConfigurations;

public interface IWorkflowConfigurationService
{
    /// <summary>A null version returns the latest version of the definition.</summary>
    Task<WorkflowConfiguration> GetAsync(string workflowDefinitionId, int? workflowVersion, CancellationToken ct);
    Task<WorkflowConfiguration> SaveAsync(SaveConfigurationRequest request, CancellationToken ct);
    Task ValidateAsync(string workflowDefinitionId, int workflowVersion, CancellationToken ct);
    Task<WorkflowConfiguration> PublishAsync(string workflowDefinitionId, int workflowVersion, CancellationToken ct);
}
