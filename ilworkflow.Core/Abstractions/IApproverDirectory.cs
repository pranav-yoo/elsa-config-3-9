namespace IlWorkflow.Core.Abstractions;

public interface IApproverDirectory
{
    Task<bool> HasRoleAsync(string id, string role, CancellationToken ct);
}
