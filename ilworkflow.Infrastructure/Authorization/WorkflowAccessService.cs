using System.Security.Claims;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Models;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;
using IlWorkflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IlWorkflow.Infrastructure.Authorization;

/// <summary>Native instance permission is broad; this adds ownership/participation checks for ticket clients.</summary>
public sealed class WorkflowAccessService(IWorkflowInstanceStore instances, IUserTaskRepository tasks,
    IUserTaskIdentityResolver identities, IUserTaskAccessPolicy access, ApplicationDbContext db)
{
    public async Task<bool> CanReadInstanceAsync(string id, ClaimsPrincipal user, CancellationToken ct)
    {
        var instance = await instances.FindAsync(new WorkflowInstanceFilter { Id = id }, ct);
        if (instance is null) return false;
        var tenant = user.FindFirstValue("tenant") ?? "";
        if ((instance.TenantId ?? "") != tenant) return false;
        if (PermissionChecks.IsSupervisor(user)) return true;
        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        if (instance.WorkflowState.Output.TryGetValue("requesterId", out var requester) && requester?.ToString() == subject) return true;
        // Workflow supervisors listed in the configuration version this instance runs.
        var configuration = await db.WorkflowConfigurations.Where(x => x.DefinitionVersionId == instance.DefinitionVersionId)
            .Select(x => x.ConfigurationJson).SingleOrDefaultAsync(ct);
        if (configuration is not null && subject is not null
            && ConfigurationJson.Read<WorkflowSetup>(configuration).Supervisors?.Contains(subject) == true) return true;
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
