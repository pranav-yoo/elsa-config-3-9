using System.Text.Json.Serialization;

namespace IlWorkflow.Contracts.WorkflowConfigurations.Requests;

/// <summary>
/// One save contract for every configuration write. Unmapped members are rejected so a retired field such as
/// <c>definition_id</c> or <c>id</c> returns 400 instead of silently creating an unrelated definition.
/// <list type="bullet">
/// <item>No <c>workflow_definition_id</c>: create a new definition with a generated ID and its first version.</item>
/// <item><c>workflow_definition_id</c> only: create that definition, or its next version when it already exists.</item>
/// <item>Both IDs: update that draft version.</item>
/// </list>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveConfigurationRequest(
    [property: JsonPropertyName("workflow_definition_id")] string? WorkflowDefinitionId,
    [property: JsonPropertyName("workflow_version_id")] string? WorkflowVersionId,
    WorkflowSetupRequest Configuration);
