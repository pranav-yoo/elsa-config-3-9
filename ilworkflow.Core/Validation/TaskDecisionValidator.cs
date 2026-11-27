using System.Text.Json;
using FluentValidation;

namespace IlWorkflow.Core.Validation;

/// <summary>The button a user pressed (<paramref name="Action"/>) and the JSON they sent with it.</summary>
public sealed record DecisionData(string Action, JsonElement Data);

/// <summary>
/// Validates the data sent when a task is completed. Elsa calls this through <c>TicketFormProvider</c>
/// for every completion, so no endpoint can skip it. Elsa itself already rejects action keys
/// that the task does not offer (HTTP 422 "invalid-action").
/// </summary>
public sealed class TaskDecisionValidator : AbstractValidator<DecisionData>
{
    /// <param name="stage">The stage of a review task, or null for a correction task.</param>
    public TaskDecisionValidator(StageDefinition? stage)
    {
        RuleFor(x => x.Data.ValueKind).Equal(JsonValueKind.Object).WithMessage("Supply a data object.");
        When(x => x.Data.ValueKind == JsonValueKind.Object, () =>
        {
            if (stage is null) RuleFor(x => x).Custom(CheckResubmit);
            else RuleFor(x => x).Custom((decision, context) => CheckReview(stage, decision, context));
        });
    }

    /// <summary>A correction task offers only RESUBMIT, with the (possibly changed) ticket attributes.</summary>
    private static void CheckResubmit(DecisionData decision, ValidationContext<DecisionData> context)
    {
        if (!decision.Data.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
            context.AddFailure("RESUBMIT requires an \"attributes\" object.");
        if (decision.Data.EnumerateObject().Any(field => field.Name != "attributes"))
            context.AddFailure("RESUBMIT accepts only \"attributes\".");
    }

    /// <summary>A review task accepts an optional comment and a reason (required when the action says so).</summary>
    private static void CheckReview(StageDefinition stage, DecisionData decision, ValidationContext<DecisionData> context)
    {
        foreach (var field in decision.Data.EnumerateObject())
            if (field.Name is not ("reason" or "comment") || field.Value.ValueKind != JsonValueKind.String)
                context.AddFailure("Only string \"reason\" and \"comment\" fields are accepted.");

        var action = stage.Actions?.FirstOrDefault(x => string.Equals(x.Key, decision.Action, StringComparison.OrdinalIgnoreCase));
        if (action is null)
        {
            context.AddFailure($"Unknown action '{decision.Action}'.");
            return;
        }

        var hasReason = decision.Data.TryGetProperty("reason", out var reason)
            && reason.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(reason.GetString());
        if (action.RequireReason && !hasReason)
            context.AddFailure($"{action.DisplayLabel} requires a reason.");
    }
}
