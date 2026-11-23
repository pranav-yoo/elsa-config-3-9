using System.Security.Cryptography;
using System.Text.Json;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Models;
using Elsa.UserTasks.Services;

namespace IlWorkflow.Infrastructure.Authorization;

public sealed class ObservedTaskAccess : IUserTaskAccessPolicy
{
    private readonly DefaultUserTaskAccessPolicy _native = new();

    // Tags are part of Elsa's public task projection. Store fingerprints so readers do not
    // receive a list of other observers' directory IDs.
    public static string TagFor(ParticipantReference subject) => "ticket-observer:" +
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new[] { subject.TenantId, subject.Provider, subject.Type.ToString(), subject.Id })));

    /// <summary>Tag of a workflow supervisor (WorkflowSetup.Supervisors), fingerprinted like observer tags.</summary>
    public static string SupervisorTagFor(ParticipantReference subject) => "ticket-supervisor:" + TagFor(subject)["ticket-observer:".Length..];

    public static bool CanObserve(UserTaskActor actor) => !actor.IsGuest && actor.HasPermission("user-tasks", "view");

    public Task<UserTaskQueryScope?> CreateScopeAsync(UserTaskActor actor, UserTaskQueryScopeKind kind, CancellationToken cancellationToken = default) =>
        _native.CreateScopeAsync(actor, kind, cancellationToken);

    public Task<bool> AuthorizeAsync(UserTask task, UserTaskActor actor, UserTaskAccessOperation operation, CancellationToken cancellationToken = default)
    {
        if (operation is (UserTaskAccessOperation.ReadSummary or UserTaskAccessOperation.ReadProtected)
            && task.TenantId == actor.Subject.TenantId && task.HealthSeverity != UserTaskHealthSeverity.Blocking
            && CanObserve(actor) && task.Tags.Contains(TagFor(actor.Subject)))
            return Task.FromResult(true);

        // Workflow supervisors: read every task of their workflows (blocked ones too, so they can fix
        // them) and reassign. Never claim, complete or cancel through this grant.
        if (!actor.IsGuest && task.TenantId == actor.Subject.TenantId && task.Tags.Contains(SupervisorTagFor(actor.Subject))
            && (operation is (UserTaskAccessOperation.ReadSummary or UserTaskAccessOperation.ReadProtected) && actor.HasPermission("user-tasks", "view")
                || operation == UserTaskAccessOperation.Assign && actor.HasPermission("user-tasks", "assign")))
            return Task.FromResult(true);

        return _native.AuthorizeAsync(task, actor, operation, cancellationToken);
    }
}
