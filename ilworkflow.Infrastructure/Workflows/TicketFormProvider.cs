using System.Text.Json;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Models;
using FluentValidation;
using IlWorkflow.Infrastructure.Configuration;

namespace IlWorkflow.Api.Workflows;

/// <summary>Native task completion always passes through this provider; direct API calls cannot skip validation.</summary>
public sealed class TicketFormProvider : IUserTaskFormProvider
{
    public string Name => "configured-ticket";
    public Task<ResolvedUserTaskForm?> ResolveAsync(UserTaskFormReference reference, CancellationToken cancellationToken = default)
    {
        var setup = ConfigurationJson.Read<WorkflowSetup>(reference.Binding!);
        return Task.FromResult<ResolvedUserTaskForm?>(new(reference, "1", new Dictionary<string, object?>())
        {
            Fields = reference.Key == "correction"
                ? setup.Attributes.Select(x => new UserTaskFormFieldDescriptor(x.Key, x.Key, x.Type.ToString(), x.Required)).ToArray()
                : [new UserTaskFormFieldDescriptor("reason", "Reason required for rejection")]
        });
    }
    public async Task<UserTaskFormValidationResult> ValidateAndNormalizeAsync(ResolvedUserTaskForm form, string actionKey, JsonElement data, CancellationToken cancellationToken = default)
    {
        // Binding carries the published snapshot pinned when this task was created. Reading the
        // latest configuration here would silently change the rules for an already pending task.
        var setup = ConfigurationJson.Read<WorkflowSetup>(form.Requested.Binding!);
        var result = await new TaskDecisionValidator(setup, form.Requested.Key == "correction").ValidateAsync(new DecisionData(actionKey, data), cancellationToken);
        return result.IsValid ? new(true, data.Clone()) : new(false, Errors: result.Errors.Select(x => x.ErrorMessage).ToArray());
    }
}
