namespace IlWorkflow.Contracts.WorkflowConfigurations.Responses;

/// <summary>Public shape of one configuration version. Revision is an internal counter that grows on every save and publish.</summary>
public sealed record WorkflowConfigurationResponse(
    string WorkflowDefinitionId,
    int WorkflowVersion,
    long Revision, bool IsPublished, WorkflowSetup Configuration);
