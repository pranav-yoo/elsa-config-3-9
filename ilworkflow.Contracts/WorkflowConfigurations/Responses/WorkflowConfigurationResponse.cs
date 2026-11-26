using System.Text.Json.Serialization;

namespace IlWorkflow.Contracts.WorkflowConfigurations.Responses;

/// <summary>Public shape of one configuration version. Revision is an internal counter that grows on every save and publish.</summary>
public sealed record WorkflowConfigurationResponse(
    [property: JsonPropertyName("workflow_definition_id")] string WorkflowDefinitionId,
    [property: JsonPropertyName("workflow_version_id")] string WorkflowVersionId,
    int VersionNumber, long Revision, bool IsPublished, WorkflowSetup Configuration);
