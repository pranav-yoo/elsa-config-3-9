using System.Security.Claims;
using Elsa.Common;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Models;
using Elsa.UserTasks.Services;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Npgsql;

namespace IlWorkflow.Api.Workflows;

/// <summary>Elsa reserves completion on a task before resuming its bookmark. Serialize both steps
/// across sibling tasks so a losing task never reserves an approval while its bookmark is being removed.</summary>
public sealed class ParallelTaskCommands(IConfiguration configuration, IUserTaskRepository tasks, IBookmarkStore bookmarks,
    IServiceProvider services, IUserTaskIdentityResolver identities, IUserTaskAccessPolicy access,
    DefaultUserTaskWorkflowResumer nativeResumer, ISystemClock clock)
{
    private string? _lockedWorkflow;

    public async Task RunCommandAsync(string taskId, bool completing, ClaimsPrincipal principal, Func<Task> command, CancellationToken ct)
    {
        var task = await tasks.GetAsync("", taskId, ct);
        if (task?.TaskType != ParallelTicketReview.TaskType)
        {
            await command();
            return;
        }

        await WithLockAsync(task.WorkflowInstanceId, async () =>
        {
            if (completing && await identities.ResolveAsync(principal, ct) is { } actor
                && await access.AuthorizeAsync(task, actor, UserTaskAccessOperation.Complete, ct))
            {
                // A host may stop after Elsa accepts a command but before it resumes. Recover
                // that decision before Elsa handles a retry or accepts a competing decision.
                var pending = await tasks.QueryAsync(new UserTaskQuery
                {
                    TenantId = task.TenantId, WorkflowInstanceId = task.WorkflowInstanceId,
                    TaskType = ParallelTicketReview.TaskType, Statuses = [UserTaskStatus.Completing], Limit = 2
                }, ct);
                foreach (var pendingTask in pending.Items)
                {
                    var operation = pendingTask.Operations.LastOrDefault(x => x.Kind == UserTaskOperationKind.Complete && x.Status == UserTaskOperationStatus.Accepted);
                    if (operation is not null)
                        await ResumeAsync(pendingTask, new UserTaskStimulus(pendingTask.TenantId, pendingTask.Id, operation.OperationId,
                            pendingTask.CompletionActionKey!, pendingTask.CompletionData, pendingTask.CompletedBy,
                            pendingTask.CompletedAt ?? operation.CreatedAt, pendingTask.BookmarkId), ct);
                    if (await HasBookmarkAsync(pendingTask, ct))
                        throw new ApiException(409, "An accepted parallel decision is still being recovered. Reload the task and retry.");
                }
                await FinalizeIfRemovedAsync(task, ct);
            }
            await command();
        }, ct);
    }

    public Task ResumeAsync(UserTask task, UserTaskStimulus stimulus, CancellationToken ct) =>
        task.TaskType != ParallelTicketReview.TaskType
            ? nativeResumer.ResumeAsync(task, stimulus, ct)
            : WithLockAsync(task.WorkflowInstanceId, async () =>
            {
                if (await HasBookmarkAsync(task, ct)) await nativeResumer.ResumeAsync(task, stimulus, ct);
                await FinalizeIfRemovedAsync(task, ct);
                await FinalizeUnansweredBranchesAsync(task, ct);
            }, ct);

    private async Task FinalizeUnansweredBranchesAsync(UserTask task, CancellationToken ct)
    {
        // The native resumer has returned from persistence. Reconcile losing task projections
        // against the committed bookmarks, including on hosts where native projection lags.
        string? cursor = null;
        do
        {
            var page = await tasks.QueryAsync(new UserTaskQuery
            {
                TenantId = task.TenantId, WorkflowInstanceId = task.WorkflowInstanceId, TaskType = ParallelTicketReview.TaskType,
                Statuses = [UserTaskStatus.Assigned, UserTaskStatus.Available, UserTaskStatus.Unassigned],
                Cursor = cursor, Limit = 100
            }, ct);
            foreach (var sibling in page.Items)
                await FinalizeIfRemovedAsync(sibling, ct);
            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    private async Task<bool> HasBookmarkAsync(UserTask task, CancellationToken ct) =>
        await bookmarks.FindAsync(new BookmarkFilter { BookmarkId = task.BookmarkId, WorkflowInstanceId = task.WorkflowInstanceId }, ct) is not null;

    private async Task FinalizeIfRemovedAsync(UserTask task, CancellationToken ct)
    {
        if (!await HasBookmarkAsync(task, ct))
            await services.GetRequiredService<IUserTaskProjectionService>()
                .FinalizeBookmarkRemovalAsync(new UserTaskBookmarkRemoval(task.TenantId, task.Id, task.BookmarkId, clock.UtcNow), ct);
    }

    private async Task WithLockAsync(string workflowId, Func<Task> action, CancellationToken ct)
    {
        // The HTTP command and its inline native resume resolve this same scoped service.
        if (_lockedWorkflow == workflowId)
        {
            await action();
            return;
        }
        await using var connection = new NpgsqlConnection(configuration.GetConnectionString("Elsa"));
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var acquire = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", connection, transaction);
        acquire.Parameters.AddWithValue("key", "parallel-review:" + workflowId);
        await acquire.ExecuteNonQueryAsync(ct);
        _lockedWorkflow = workflowId;
        try { await action(); }
        finally { _lockedWorkflow = null; }
        // Disposing the lock transaction releases it even when the native command fails.
    }
}

public sealed class ParallelTaskWorkflowResumer(ParallelTaskCommands commands) : IUserTaskWorkflowResumer
{
    public Task ResumeAsync(UserTask task, UserTaskStimulus stimulus, CancellationToken cancellationToken = default) =>
        commands.ResumeAsync(task, stimulus, cancellationToken);
}
