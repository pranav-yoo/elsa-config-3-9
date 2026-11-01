using Elsa.Identity.Contracts;
using Elsa.Identity.Models;
using IlWorkflow.Api.ExternalIdentity;

namespace IlWorkflow.Api.Tickets;

/// <summary>External eligibility uses the master-backed GET data; local accounts still use native Elsa Identity.</summary>
public sealed class TicketIdentity(IUserProvider users, DemoMemberDirectory directory)
{
    public async Task<bool> HasRoleAsync(string id, string role, CancellationToken ct)
    {
        if (id.StartsWith("external:", StringComparison.Ordinal))
            return role == "Approver" && directory.FindByParticipantId(id) is not null;
        var user = await users.FindAsync(new UserFilter { Id = id }, ct);
        return user?.Roles.Contains(role) == true;
    }
}
