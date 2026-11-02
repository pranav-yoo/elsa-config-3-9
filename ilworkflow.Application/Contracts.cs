using System.Text.Json;
using System.Text.Json.Serialization;
namespace IlWorkflow.Application.Configuration;

public sealed record SaveConfigurationRequest(long? Id, long? ExpectedRevision, WorkflowSetup Configuration);
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
