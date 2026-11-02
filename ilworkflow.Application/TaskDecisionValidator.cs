using System.Text.Json;
using FluentValidation;
namespace IlWorkflow.Application.Configuration;

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

