using System.Text.Json;
using FluentValidation;
namespace IlWorkflow.Application.Configuration;

public sealed record DecisionData(string Action, JsonElement Data);
public sealed class TaskDecisionValidator : AbstractValidator<DecisionData>
{
    /// <param name="stageKey">Stage of a review task; null for tasks created before custom actions (APPROVE/REJECT only).</param>
    public TaskDecisionValidator(WorkflowSetup setup, bool correction, string? stageKey = null)
    {
        var stage = stageKey is null ? null : setup.Stages.FirstOrDefault(x => x.Key == stageKey);
        var offered = stage?.Actions ?? [new StageAction(TaskActionKeys.Approve, "Approve", null!), new StageAction(TaskActionKeys.Reject, "Reject", null!)];
        RuleFor(x => x).Custom((request, context) =>
        {
            if (request.Data.ValueKind != JsonValueKind.Object) { context.AddFailure("Supply a data object."); return; }
            var decision = TaskActionKeys.Parse(request.Action);
            if (correction)
            {
                if (decision != TaskDecision.Resubmit || !request.Data.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
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
                // REJECT always needs a reason; custom actions when configured with requireReason.
                if ((decision == TaskDecision.Reject || action.RequireReason)
                    && (!request.Data.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(reason.GetString())))
                    context.AddFailure(decision == TaskDecision.Reject ? "Rejection requires a reason." : $"{action.DisplayLabel} requires a reason.");
            }
        });
    }
}

