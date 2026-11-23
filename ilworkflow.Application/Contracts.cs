using System.Text.Json;
using System.Text.Json.Serialization;
namespace IlWorkflow.Application.Configuration;

public sealed record SaveConfigurationRequest(long? Id, long? ExpectedRevision, WorkflowSetupRequest Configuration,
    [property: JsonPropertyName("definition_id")] string? DefinitionId = null);
public sealed record WorkflowSetupRequest(string Name, string StartStage, StageDefinition[] Stages, string[]? Supervisors = null);
public sealed record RevisionRequest(long ExpectedRevision);

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
    Task<WorkflowConfiguration> GetAsync(long id, CancellationToken ct);
    Task<WorkflowConfiguration> SaveAsync(SaveConfigurationRequest request, CancellationToken ct);
    Task ValidateAsync(long id, CancellationToken ct);
    Task<WorkflowConfiguration> PublishAsync(long id, long expectedRevision, CancellationToken ct);
    Task<WorkflowConfiguration> CreateNextDraftAsync(long id, long expectedRevision, CancellationToken ct);
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
