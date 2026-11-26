namespace IlWorkflow.Application.Abstractions;

public interface IApproverDirectory
{
    Task<bool> HasRoleAsync(string id, string role, CancellationToken ct);
}
