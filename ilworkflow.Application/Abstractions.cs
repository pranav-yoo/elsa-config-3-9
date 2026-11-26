using System.Text.Json;
using System.Text.Json.Serialization;
namespace IlWorkflow.Application.Configuration;

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


// Application ports keep validation independent of Elsa and EF Core. HTTP request/response models live in ilworkflow.Contracts.
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

public interface IWorkflowAttributeService
{
    Task<WorkflowAttribute> SaveAsync(SaveWorkflowAttributeRequest request, CancellationToken ct);
    Task<IReadOnlyList<WorkflowAttribute>> ListAsync(string definitionId, CancellationToken ct);
}
