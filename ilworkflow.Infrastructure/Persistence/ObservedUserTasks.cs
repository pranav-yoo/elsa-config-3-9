using System.Text.Json;
using Elsa.Persistence.EFCore;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Models;
using Elsa.UserTasks.Persistence.EFCore;
using Elsa.UserTasks.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace IlWorkflow.Infrastructure.Persistence;

public sealed class ObservedUserTasks(Store<UserTasksElsaDbContext, UserTaskRecord> store, IUserTaskRepository tasks, IUserTaskAccessPolicy access)
{
    public async Task<UserTaskQueryResultDto> QueryAsync(UserTaskActor actor, string? workflowInstanceId, UserTaskStatus? status,
        string? cursor, int limit, bool includeTotalCount, CancellationToken ct)
    {
        if (!ObservedTaskAccess.CanObserve(actor)) throw new ApiException(403, "Task view permission is required.");

        await using var db = await store.CreateDbContextAsync(ct);
        var entity = db.UserTasks.EntityType;
        var table = db.GetService<ISqlGenerationHelper>().DelimitIdentifier(entity.GetTableName()!, entity.GetSchema());
        // Elsa stores tags in a text column in 3.9. Exact JSON containment filters membership in
        // PostgreSQL before counting or paginating, without a separate observer table.
        var sql = $"SELECT * FROM {table} WHERE \"TagsJson\"::jsonb @> CAST({{0}} AS jsonb)";
        var tag = JsonSerializer.Serialize(new[] { ObservedTaskAccess.TagFor(actor.Subject) });
        var records = db.UserTasks.FromSqlRaw(sql, tag).AsNoTracking()
            .Where(task => task.TenantId == actor.Subject.TenantId && task.HealthSeverity != UserTaskHealthSeverity.Blocking);
        if (!string.IsNullOrEmpty(workflowInstanceId)) records = records.Where(task => task.WorkflowInstanceId == workflowInstanceId);
        if (status.HasValue) records = records.Where(task => task.Status == status.Value);
        int? total = includeTotalCount ? await records.CountAsync(ct) : null;
        if (!string.IsNullOrEmpty(cursor)) records = records.Where(task => string.Compare(task.Id, cursor) > 0);
        var ids = await records.OrderBy(task => task.Id).Select(task => task.Id).Take(limit + 1).ToArrayAsync(ct);

        var items = new List<UserTaskSummary>(limit);
        foreach (var id in ids.Take(limit))
        {
            var task = await tasks.GetAsync(actor.Subject.TenantId, id, ct);
            if (task is not null && await access.AuthorizeAsync(task, actor, UserTaskAccessOperation.ReadSummary, ct))
                items.Add(await UserTaskModelMapper.ToSummaryAsync(task, actor, access, ct));
        }
        return new UserTaskQueryResultDto(items, ids.Length > limit ? ids[limit - 1] : null, total);
    }
}
