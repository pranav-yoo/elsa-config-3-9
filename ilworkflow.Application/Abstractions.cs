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
    /// <summary>A null version returns the latest version of the definition.</summary>
    Task<WorkflowConfiguration> GetAsync(string workflowDefinitionId, int? workflowVersion, CancellationToken ct);
    Task<WorkflowConfiguration> SaveAsync(SaveConfigurationRequest request, CancellationToken ct);
    Task ValidateAsync(string workflowDefinitionId, int workflowVersion, CancellationToken ct);
    Task<WorkflowConfiguration> PublishAsync(string workflowDefinitionId, int workflowVersion, CancellationToken ct);
}
