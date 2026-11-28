using System.Security.Claims;
using Elsa.UserTasks.Contracts;
using FluentValidation;
using Elsa.UserTasks.Models;
using IlWorkflow.Infrastructure.Workflows;

namespace IlWorkflow.Infrastructure.Tasks;

/// <summary>Result of a task command: Accepted (HTTP 202) with the updated task, or a conflict (HTTP 409).</summary>
public sealed record TaskCommandResult(bool Accepted, object Body);

/// <summary>
/// Application wrapper over Elsa's native user-task manager. Add company authentication/validation in front of
/// these methods. Every native rule still applies: candidate/owner checks, revisions, operation idempotency,
/// and form validation (TicketFormProvider).
/// </summary>
public sealed class TaskService(IUserTaskManager manager, IUserTaskIdentityResolver identities, IUserTaskRepository tasks)
{
    private const string Tenant = "";
    private static readonly System.Text.Json.JsonElement EmptyData = System.Text.Json.JsonSerializer.SerializeToElement(new { });

    public async Task<UserTaskQueryResultDto> ListAsync(ClaimsPrincipal user, ListTasksRequest request, CancellationToken ct)
    {
        var actor = await ActorAsync(user, "view", ct);
        await new ListTasksRequestValidator().ValidateAndThrowAsync(request, ct);
        var scope = request.Scope.ToLowerInvariant();
        UserTaskStatus? status = string.IsNullOrWhiteSpace(request.Status) ? null : Enum.Parse<UserTaskStatus>(request.Status, true);

        var kind = scope switch
        {
            "assigned" => UserTaskQueryScopeKind.Assigned,
            "available" => UserTaskQueryScopeKind.Available,
            "history" => UserTaskQueryScopeKind.History,
            _ => UserTaskQueryScopeKind.All
        };
        return await manager.QueryAsync(new UserTaskQuery
        {
            TenantId = Tenant, Limit = request.Limit, Cursor = request.Cursor, WorkflowInstanceId = request.WorkflowInstanceId,
            Statuses = status is { } st ? [st] : [], IncludeTotalCount = request.IncludeTotalCount,
            Sort = "created", Descending = true
        }, kind, actor, ct);
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
        return await RunAsync(() => manager.ClaimAsync(Tenant, taskId, mutation, actor, ct));
    }

    public async Task<TaskCommandResult> ReleaseAsync(ClaimsPrincipal user, string taskId, TaskCommandRequest request, CancellationToken ct)
    {
        var actor = await ActorAsync(user, "claim", ct);
        var mutation = new UserTaskMutationRequest(await RevisionAsync(taskId, request.ExpectedRevision, ct), OperationId(request.OperationId));
        return await RunAsync(() => manager.ReleaseAsync(Tenant, taskId, mutation, actor, ct));
    }

    public async Task<TaskCommandResult> AssignAsync(ClaimsPrincipal user, string taskId, AssignTaskRequest request, CancellationToken ct)
    {
        await new AssignTaskRequestValidator().ValidateAndThrowAsync(request, ct);
        var actor = await ActorAsync(user, "assign", ct);
        var assign = new UserTaskAssignRequest(await RevisionAsync(taskId, request.ExpectedRevision, ct), TicketState.User(request.UserId),
            request.Reason!, OperationId(request.OperationId));
        return await RunAsync(() => manager.AssignAsync(Tenant, taskId, assign, actor, ct));
    }

    public async Task<TaskCommandResult> CompleteAsync(ClaimsPrincipal user, string taskId, CompleteTaskRequest request, CancellationToken ct)
    {
        await new CompleteTaskRequestValidator().ValidateAndThrowAsync(request, ct);
        var actor = await ActorAsync(user, "complete", ct);
        // Data is validated by the task's form provider (reason/comment rules, RESUBMIT attributes) inside Elsa.
        var completion = new UserTaskCompletionRequest(await RevisionAsync(taskId, request.ExpectedRevision, ct), OperationId(request.OperationId),
            request.ActionKey, request.Data ?? EmptyData);
        return await RunAsync(() => manager.CompleteAsync(Tenant, taskId, completion, actor, ct));
    }

    // Same checks Elsa's endpoints apply: authenticated, default organization, permission for the operation.
    private async Task<UserTaskActor> ActorAsync(ClaimsPrincipal user, string verb, CancellationToken ct)
    {
        if (user.Identity?.IsAuthenticated != true) throw new ApiException(401, "A valid bearer token is required.");
        if (!string.IsNullOrEmpty(user.FindFirstValue("tenant"))) throw new ApiException(403, "This version supports the default organization only.");
        if (!PermissionChecks.HasPermission(user, "user-tasks", verb)) throw new ApiException(403, $"Permission user-tasks:{verb} is required.");
        return await identities.ResolveAsync(user, ct) ?? throw new ApiException(403, "Unknown task user.");
    }

    private async Task<int> RevisionAsync(string taskId, int? expected, CancellationToken ct) =>
        expected ?? (await tasks.GetAsync(Tenant, taskId, ct))?.Revision ?? throw new ApiException(404, "Task not found.");

    private static string OperationId(string? operationId) => string.IsNullOrWhiteSpace(operationId) ? Guid.NewGuid().ToString() : operationId;

    private static async Task<TaskCommandResult> RunAsync(Func<Task<UserTaskOperationResult>> command)
    {
        UserTaskOperationResult result;
        try { result = await command(); }
        catch (UserTaskRevisionConflictException) { throw new ApiException(409, "The task changed. Read it again and retry with the current revision."); }
        return result.Accepted
            ? new(true, new { task = result.Task, operation = result.Operation })
            : new(false, new { code = result.ConflictCode ?? "conflict", task = result.Task, operation = result.Operation });
    }
}
