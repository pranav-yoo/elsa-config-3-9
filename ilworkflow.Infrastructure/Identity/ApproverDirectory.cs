using Elsa.Identity.Contracts;
using Elsa.Identity.Models;

namespace IlWorkflow.Infrastructure.Identity;

/// <summary>Approver eligibility comes from native Elsa Identity roles.</summary>
public sealed class ApproverDirectory(IUserProvider users) : IApproverDirectory
{
    public async Task<bool> HasRoleAsync(string id, string role, CancellationToken ct)
    {
        var user = await users.FindAsync(new UserFilter { Id = id }, ct);
        return user?.Roles.Contains(role) == true;
    }
}
