using System.Text.Json;
using FluentValidation;

namespace IlWorkflow.Application.Validation;

public sealed record DecisionData(string Action, JsonElement Data);

/// <summary>Validates the button a user pressed: it must be one of the stage's configured actions (or RESUBMIT on a correction task).</summary>
public sealed class TaskDecisionValidator : AbstractValidator<DecisionData>
{
    /// <param name="stageKey">Stage of a review task; null for a correction task.</param>
    public TaskDecisionValidator(WorkflowSetup setup, bool correction, string? stageKey = null)
    {
        var offered = setup.Stages.FirstOrDefault(x => x.Key == stageKey)?.Actions ?? [];
        RuleFor(x => x).Custom((request, context) =>
        {
            if (request.Data.ValueKind != JsonValueKind.Object) { context.AddFailure("Supply a data object."); return; }
            if (correction)
            {
                if (!string.Equals(request.Action, TaskActionKeys.Resubmit, StringComparison.OrdinalIgnoreCase)
                    || !request.Data.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
                { context.AddFailure("Resubmit requires a complete attributes object."); return; }
                if (request.Data.EnumerateObject().Any(x => x.Name != "attributes")) context.AddFailure("Unknown correction field.");
                var values = attributes.Deserialize<Dictionary<string, JsonElement>>()!;
                foreach (var failure in new TicketInputValidator(setup).Validate(values).Errors) context.AddFailure(failure);
            }
            else
            {
                var action = offered.FirstOrDefault(x => string.Equals(x.Key, request.Action, StringComparison.OrdinalIgnoreCase));
                if (action is null) { context.AddFailure("Unknown action. Use one of: " + string.Join(", ", offered.Select(x => x.Key)) + "."); return; }
                foreach (var field in request.Data.EnumerateObject())
                    if (field.Name is not ("reason" or "comment") || field.Value.ValueKind != JsonValueKind.String) context.AddFailure("Only string reason/comment fields are accepted; attributes are frozen.");
                if (action.RequireReason
                    && (!request.Data.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(reason.GetString())))
                    context.AddFailure($"{action.DisplayLabel} requires a reason.");
            }
        });
    }
}
