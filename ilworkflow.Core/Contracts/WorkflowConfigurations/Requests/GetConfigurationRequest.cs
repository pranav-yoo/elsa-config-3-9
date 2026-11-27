using System.Text.Json.Serialization;

namespace IlWorkflow.Core.Contracts.WorkflowConfigurations.Requests;

/// <summary><c>workflow_version</c> is optional: omit it to read the latest version of the definition.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GetConfigurationRequest(string? WorkflowDefinitionId, int? WorkflowVersion);
