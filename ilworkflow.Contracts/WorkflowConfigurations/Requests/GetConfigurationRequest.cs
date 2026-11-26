using System.Text.Json.Serialization;

namespace IlWorkflow.Contracts.WorkflowConfigurations.Requests;

/// <summary><c>workflow_version</c> is optional: omit it to read the latest version of the definition.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GetConfigurationRequest(
    [property: JsonPropertyName("workflow_definition_id")] string? WorkflowDefinitionId,
    [property: JsonPropertyName("workflow_version")] int? WorkflowVersion);
