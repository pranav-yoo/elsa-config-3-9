using System.Text.Json;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Models;

namespace IlWorkflow.Infrastructure.Workflows;

/// <summary>
/// Elsa calls this provider for EVERY task completion (through /api/tasks or the native /elsa/api/user-tasks),
/// so the business rules in <see cref="TaskDecisionValidator"/> can never be skipped.
/// </summary>
public sealed class TicketFormProvider : IUserTaskFormProvider
{
    public const string ProviderName = "configured-ticket";
    public const string CorrectionFormKey = "correction";
    private const string ReviewPrefix = "review:";

    /// <summary>Review tasks carry their stage in the form key, for example "review:B".</summary>
    public static string ReviewFormKey(string stageKey) => ReviewPrefix + stageKey;

    public string Name => ProviderName;

    public Task<ResolvedUserTaskForm?> ResolveAsync(UserTaskFormReference reference, CancellationToken cancellationToken = default)
    {
        UserTaskFormFieldDescriptor[] fields = reference.Key == CorrectionFormKey
            ? [new("attributes", "Attributes")]
            : [new("reason", "Reason"), new("comment", "Comment")];
        return Task.FromResult<ResolvedUserTaskForm?>(new(reference, "1", new Dictionary<string, object?>()) { Fields = fields });
    }

    public async Task<UserTaskFormValidationResult> ValidateAndNormalizeAsync(ResolvedUserTaskForm form, string actionKey, JsonElement data,
        CancellationToken cancellationToken = default)
    {
        // Binding is the setup snapshot saved when the task was created, so rules cannot change for a pending task.
        var setup = ConfigurationJson.Read<WorkflowSetup>(form.Requested.Binding!);
        var key = form.Requested.Key;
        var stage = key.StartsWith(ReviewPrefix, StringComparison.Ordinal) ? setup.FindStage(key[ReviewPrefix.Length..]) : null;

        var result = await new TaskDecisionValidator(stage).ValidateAsync(new DecisionData(actionKey, data), cancellationToken);
        return result.IsValid
            ? new(true, data.Clone())
            : new(false, Errors: result.Errors.Select(error => error.ErrorMessage).ToArray());
    }
}
