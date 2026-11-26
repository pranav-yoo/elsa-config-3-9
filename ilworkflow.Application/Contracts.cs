using System.Text.Json;
using System.Text.Json.Serialization;
namespace IlWorkflow.Application.Configuration;

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
public sealed record WorkflowSetupRequest(string Name, string StartStage, StageDefinition[] Stages, string[]? Supervisors = null);

/// <summary>Public shape of one configuration version. Revision is an internal counter that grows on every save and publish.</summary>
public sealed record WorkflowConfigurationResponse(
    [property: JsonPropertyName("workflow_definition_id")] string WorkflowDefinitionId,
    [property: JsonPropertyName("workflow_version_id")] string WorkflowVersionId,
    int VersionNumber, long Revision, bool IsPublished, WorkflowSetup Configuration);

public static class ConfigurationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new ArgumentException("A JSON object is required.");
}

public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}


// Application ports keep validation and HTTP contracts independent of Elsa and EF Core.
public interface IApproverDirectory
{
    Task<bool> HasRoleAsync(string id, string role, CancellationToken ct);
}
public interface IWorkflowConfigurationService
{
    Task<WorkflowConfiguration> GetAsync(string workflowDefinitionId, string workflowVersionId, CancellationToken ct);
    Task<WorkflowConfiguration> SaveAsync(SaveConfigurationRequest request, CancellationToken ct);
    Task ValidateAsync(string workflowDefinitionId, string workflowVersionId, CancellationToken ct);
    Task<WorkflowConfiguration> PublishAsync(string workflowDefinitionId, string workflowVersionId, CancellationToken ct);
}

// /api/tasks wrapper requests. ExpectedRevision/OperationId are optional: omitted values use the task's
// current revision and a new operation ID. Send the original OperationId to retry the same command safely.
public sealed record TaskCommandRequest(int? ExpectedRevision = null, string? OperationId = null);
public sealed record CompleteTaskRequest(string ActionKey, JsonElement? Data = null, int? ExpectedRevision = null, string? OperationId = null);
public sealed record AssignTaskRequest(string UserId, string? Reason = null, int? ExpectedRevision = null, string? OperationId = null);

public sealed record SaveWorkflowAttributeRequest(long? Id, string DefinitionId, AttributeDefinition Attribute, AttributeStatus Status = AttributeStatus.Active);
public interface IWorkflowAttributeService
{
    Task<WorkflowAttribute> SaveAsync(SaveWorkflowAttributeRequest request, CancellationToken ct);
    Task<IReadOnlyList<WorkflowAttribute>> ListAsync(string definitionId, CancellationToken ct);
}
