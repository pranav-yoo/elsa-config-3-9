using System.Text.Json;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Models;
using FluentValidation;
using IlWorkflow.Api.Configuration;

namespace IlWorkflow.Api.Workflows;

public sealed record DecisionData(string Action, JsonElement Data);
public sealed class TaskDecisionValidator : AbstractValidator<DecisionData>
{
    public TaskDecisionValidator(WorkflowSetup setup, bool correction)
    {
        RuleFor(x => x).Custom((request, context) =>
        {
            if (request.Data.ValueKind != JsonValueKind.Object) { context.AddFailure("Supply a data object."); return; }
            if (correction)
            {
                if (request.Action != nameof(TaskDecision.Resubmit) || !request.Data.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
                { context.AddFailure("Resubmit requires a complete attributes object."); return; }
                if (request.Data.EnumerateObject().Any(x => x.Name != "attributes")) context.AddFailure("Unknown correction field.");
                var values = attributes.Deserialize<Dictionary<string, JsonElement>>()!;
                foreach (var failure in new TicketInputValidator(setup).Validate(values).Errors) context.AddFailure(failure);
            }
            else
            {
                if (request.Action is not (nameof(TaskDecision.Approve) or nameof(TaskDecision.Reject))) context.AddFailure("Approve or Reject is required.");
                foreach (var field in request.Data.EnumerateObject())
                    if (field.Name is not ("reason" or "comment") || field.Value.ValueKind != JsonValueKind.String) context.AddFailure("Only string reason/comment fields are accepted; attributes are frozen.");
                if (request.Action == nameof(TaskDecision.Reject) && (!request.Data.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(reason.GetString()))) context.AddFailure("Rejection requires a reason.");
            }
        });
    }
}

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
