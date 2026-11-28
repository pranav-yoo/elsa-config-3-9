namespace IlWorkflow.Core.Tasks;

public interface IApproverDirectory
{
    Task<bool> HasRoleAsync(string id, string role, CancellationToken ct);
}
