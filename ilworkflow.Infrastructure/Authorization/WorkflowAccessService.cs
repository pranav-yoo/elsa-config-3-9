using System.Security.Claims;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Models;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;
using IlWorkflow.Infrastructure.Configuration;

namespace IlWorkflow.Infrastructure.Authorization;

/// <summary>Native instance permission is broad; this adds ownership/participation checks for ticket clients.</summary>
public sealed class WorkflowAccessService(IWorkflowInstanceStore instances, IUserTaskRepository tasks,
    IUserTaskIdentityResolver identities, IUserTaskAccessPolicy access)
{
    public async Task<bool> CanReadInstanceAsync(string id, ClaimsPrincipal user, CancellationToken ct)
    {
        var instance = await instances.FindAsync(new WorkflowInstanceFilter { Id = id }, ct);
        if (instance is null) return false;
        var tenant = user.FindFirstValue("tenant") ?? "";
        if ((instance.TenantId ?? "") != tenant) return false;
        if (ConfigurationAccess.IsSupervisor(user)) return true;
        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        if (instance.WorkflowState.Output.TryGetValue("requesterId", out var requester) && requester?.ToString() == subject) return true;
        var actor = await identities.ResolveAsync(user, ct);
        if (actor is null) return false;
        // Ask Elsa's scoped repository whether this actor participates; never fetch all tasks and
        // filter after pagination. History grants previous participants access to their ticket journey.
        foreach (var kind in new[] { UserTaskQueryScopeKind.Assigned, UserTaskQueryScopeKind.Available, UserTaskQueryScopeKind.History })
        {
            var scope = await access.CreateScopeAsync(actor, kind, ct);
            if (scope is null) continue;
            var result = await tasks.QueryAsync(new UserTaskQuery { TenantId = tenant, Scope = scope, WorkflowInstanceId = id, Limit = 1 }, ct);
            if (result.Items.Count > 0) return true;
        }
        return false;
    }
}
