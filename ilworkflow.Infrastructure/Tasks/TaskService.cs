using System.Security.Claims;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Models;
using IlWorkflow.Api.Workflows;
using IlWorkflow.Infrastructure.Persistence;

namespace IlWorkflow.Infrastructure.Tasks;

/// <summary>Result of a task command: Accepted (HTTP 202) with the updated task, or a conflict (HTTP 409).</summary>
public sealed record TaskCommandResult(bool Accepted, object Body);

/// <summary>
/// Application wrapper over Elsa's native user-task manager. Add company authentication/validation in front of
/// these methods. Every native rule still applies: candidate/owner checks, revisions, operation idempotency,
/// form validation (TicketFormProvider) and parallel-review locking.
/// </summary>
public sealed class TaskService(IUserTaskManager manager, IUserTaskIdentityResolver identities, IUserTaskRepository tasks,
    IServiceProvider services)
{
    private const string Tenant = "";

    public async Task<UserTaskQueryResultDto> ListAsync(ClaimsPrincipal user, string scope, string? status, string? workflowInstanceId,
        string? cursor, int limit, bool includeTotalCount, CancellationToken ct)
    {
        var actor = await ActorAsync(user, "view", ct);
        if (limit is < 1 or > 100) throw new ApiException(400, "limit must be between 1 and 100.");
        UserTaskStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
            parsedStatus = Enum.TryParse<UserTaskStatus>(status, true, out var s) && Enum.IsDefined(s) ? s : throw new ApiException(400, "Unknown task status.");

        switch (scope.ToLowerInvariant())
        {
            // Read-only scopes of this application: viewableByStages and workflow supervisors.
            case "observed" or "supervised":
                var observed = services.GetService<ObservedUserTasks>() ?? throw new ApiException(503, "Observed/supervised lists require PostgreSQL.");
                return await observed.QueryAsync(actor, workflowInstanceId, parsedStatus, cursor, Math.Min(limit, 50), includeTotalCount, ct,
                    supervised: scope.Equals("supervised", StringComparison.OrdinalIgnoreCase));
            default:
                var kind = scope.ToLowerInvariant() switch
                {
                    "assigned" => UserTaskQueryScopeKind.Assigned,
                    "available" => UserTaskQueryScopeKind.Available,
                    "history" => UserTaskQueryScopeKind.History,
                    "all" => UserTaskQueryScopeKind.All,
                    _ => throw new ApiException(400, "scope must be assigned, available, history, all, observed or supervised.")
                };
                return await manager.QueryAsync(new UserTaskQuery
                {
                    TenantId = Tenant, Limit = limit, Cursor = cursor, WorkflowInstanceId = workflowInstanceId,
                    Statuses = parsedStatus is { } st ? [st] : [], IncludeTotalCount = includeTotalCount,
                    Sort = "created", Descending = true
                }, kind, actor, ct);
        }
    }

    public async Task<UserTaskDetail> GetAsync(ClaimsPrincipal user, string taskId, CancellationToken ct) =>
        await manager.GetAsync(Tenant, taskId, await ActorAsync(user, "view", ct), ct) ?? throw new ApiException(404, "Task not found.");

    public async Task<UserTaskEventsResult> EventsAsync(ClaimsPrincipal user, string taskId, string? cursor, int limit, CancellationToken ct) =>
        await manager.GetEventsAsync(Tenant, taskId, cursor!, Math.Clamp(limit, 1, 100), await ActorAsync(user, "view", ct), ct)
            ?? throw new ApiException(404, "Task not found.");

    public async Task<TaskCommandResult> ClaimAsync(ClaimsPrincipal user, string taskId, TaskCommandRequest request, CancellationToken ct)
    {
        var actor = await ActorAsync(user, "claim", ct);
        var mutation = new UserTaskMutationRequest(await RevisionAsync(taskId, request.ExpectedRevision, ct), OperationId(request.OperationId));
        return await RunAsync(user, taskId, false, () => manager.ClaimAsync(Tenant, taskId, mutation, actor, ct), ct);
    }

    public async Task<TaskCommandResult> ReleaseAsync(ClaimsPrincipal user, string taskId, TaskCommandRequest request, CancellationToken ct)
    {
        var actor = await ActorAsync(user, "claim", ct);
        var mutation = new UserTaskMutationRequest(await RevisionAsync(taskId, request.ExpectedRevision, ct), OperationId(request.OperationId));
        return await RunAsync(user, taskId, false, () => manager.ReleaseAsync(Tenant, taskId, mutation, actor, ct), ct);
    }

    public async Task<TaskCommandResult> AssignAsync(ClaimsPrincipal user, string taskId, AssignTaskRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId)) throw new ApiException(400, "userId is required.");
        var actor = await ActorAsync(user, "assign", ct);
        var assign = new UserTaskAssignRequest(await RevisionAsync(taskId, request.ExpectedRevision, ct), TicketState.User(request.UserId),
            request.Reason!, OperationId(request.OperationId));
        return await RunAsync(user, taskId, false, () => manager.AssignAsync(Tenant, taskId, assign, actor, ct), ct);
    }

    public async Task<TaskCommandResult> CompleteAsync(ClaimsPrincipal user, string taskId, CompleteTaskRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ActionKey)) throw new ApiException(400, "actionKey is required.");
        var actor = await ActorAsync(user, "complete", ct);
        // Data is validated by the task's form provider (reason/comment rules, RESUBMIT attributes) inside Elsa.
        var completion = new UserTaskCompletionRequest(await RevisionAsync(taskId, request.ExpectedRevision, ct), OperationId(request.OperationId),
            request.ActionKey, request.Data ?? System.Text.Json.JsonDocument.Parse("{}").RootElement);
        return await RunAsync(user, taskId, true, () => manager.CompleteAsync(Tenant, taskId, completion, actor, ct), ct);
    }

    // Same checks Elsa's endpoints apply: authenticated, default organization, permission for the operation.
    private async Task<UserTaskActor> ActorAsync(ClaimsPrincipal user, string verb, CancellationToken ct)
    {
        if (user.Identity?.IsAuthenticated != true) throw new ApiException(401, "A valid bearer token is required.");
        if (!string.IsNullOrEmpty(user.FindFirstValue("tenant"))) throw new ApiException(403, "This version supports the default organization only.");
        if (!ConfigurationAccess.HasPermission(user, "user-tasks", verb)) throw new ApiException(403, $"Permission user-tasks:{verb} is required.");
        return await identities.ResolveAsync(user, ct) ?? throw new ApiException(403, "Unknown task user.");
    }

    private async Task<int> RevisionAsync(string taskId, int? expected, CancellationToken ct) =>
        expected ?? (await tasks.GetAsync(Tenant, taskId, ct))?.Revision ?? throw new ApiException(404, "Task not found.");

    private static string OperationId(string? operationId) => string.IsNullOrWhiteSpace(operationId) ? Guid.NewGuid().ToString() : operationId;

    // Parallel review tasks are serialized per workflow instance (see ParallelTaskCommands); others run directly.
    private async Task<TaskCommandResult> RunAsync(ClaimsPrincipal user, string taskId, bool completing,
        Func<Task<UserTaskOperationResult>> command, CancellationToken ct)
    {
        UserTaskOperationResult? result = null;
        try
        {
            if (services.GetService<ParallelTaskCommands>() is { } parallel)
                await parallel.RunCommandAsync(taskId, completing, user, async () => result = await command(), ct);
            else result = await command();
        }
        catch (UserTaskRevisionConflictException) { throw new ApiException(409, "The task changed. Read it again and retry with the current revision."); }
        return result!.Accepted
            ? new(true, new { task = result.Task, operation = result.Operation })
            : new(false, new { code = result.ConflictCode ?? "conflict", task = result.Task, operation = result.Operation });
    }
}
