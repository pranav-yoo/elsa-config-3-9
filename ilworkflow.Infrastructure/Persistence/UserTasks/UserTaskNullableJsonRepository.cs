using System.Text.Json;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Models;
using Elsa.UserTasks.Persistence.EFCore.Repositories;
using Microsoft.EntityFrameworkCore;

namespace IlWorkflow.Infrastructure.Persistence.UserTasks;

/// <summary>
/// Elsa 3.9's DeserializeJson("null") returns a nullable containing Undefined rather than null.
/// Normalize only those values before REST serialization. All storage, revision and task logic stays native.
/// </summary>
public sealed class UserTaskNullableJsonRepository(EFCoreUserTaskRepository inner) : IUserTaskRepository
{
    private static UserTask? Normalize(UserTask? task)
    {
        if (task is null) return null;
        if (task.TaskData?.ValueKind == JsonValueKind.Undefined) task.TaskData = null;
        if (task.CompletionData?.ValueKind == JsonValueKind.Undefined) task.CompletionData = null;
        for (var i = 0; i < task.Operations.Count; i++)
            if (task.Operations[i].Data?.ValueKind == JsonValueKind.Undefined) task.Operations[i] = task.Operations[i] with { Data = null };
        return task;
    }
    public async Task<UserTask?> GetAsync(string tenantId, string taskId, CancellationToken cancellationToken = default) => Normalize(await inner.GetAsync(tenantId, taskId, cancellationToken));
    public async Task<UserTask?> FindByMaterializationKeyAsync(string tenantId, string key, CancellationToken cancellationToken = default) => Normalize(await inner.FindByMaterializationKeyAsync(tenantId, key, cancellationToken));
    public async Task<UserTask?> FindByBookmarkIdAsync(string tenantId, string bookmarkId, CancellationToken cancellationToken = default) => Normalize(await inner.FindByBookmarkIdAsync(tenantId, bookmarkId, cancellationToken));
    public async Task<UserTaskQueryResult> QueryAsync(UserTaskQuery query, CancellationToken cancellationToken = default)
    {
        var result = await inner.QueryAsync(query, cancellationToken);
        foreach (var task in result.Items) Normalize(task);
        return result;
    }
    public async Task<(UserTask Task, UserTaskInvitation Invitation)?> FindByInvitationTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        var result = await inner.FindByInvitationTokenHashAsync(tokenHash, cancellationToken);
        if (result.HasValue) Normalize(result.Value.Task);
        return result;
    }
    public Task SaveAsync(UserTask task, int expectedRevision, CancellationToken cancellationToken = default) => inner.SaveAsync(Normalize(task)!, expectedRevision, cancellationToken);
    public Task AddProjectionAsync(UserTask task, CancellationToken cancellationToken = default) => inner.AddProjectionAsync(Normalize(task)!, cancellationToken);
    public Task AppendEventAsync(string tenantId, string taskId, UserTaskEvent @event, CancellationToken cancellationToken = default) => inner.AppendEventAsync(tenantId, taskId, @event, cancellationToken);
    public Task<bool> TryMutateAsync(string tenantId, string taskId, int expectedRevision, Func<UserTask, bool> mutation, CancellationToken cancellationToken = default) =>
        inner.TryMutateAsync(tenantId, taskId, expectedRevision, task => mutation(Normalize(task)!), cancellationToken);
}
