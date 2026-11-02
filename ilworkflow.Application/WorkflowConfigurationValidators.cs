using System.Text.Json;
using FluentValidation;
namespace IlWorkflow.Application.Configuration;

public sealed class TicketInputValidator : AbstractValidator<Dictionary<string, JsonElement>>
{
    public TicketInputValidator(WorkflowSetup setup)
    {
        RuleFor(x => x).Custom((values, context) =>
        {
            foreach (var key in values.Keys.Where(key => !setup.Attributes.Any(field => field.Key == key)))
                context.AddFailure(key, "Unknown attribute.");
            foreach (var field in setup.Attributes)
            {
                values.TryGetValue(field.Key, out var value);
                if (!AttributeRules.IsValid(field, value)) context.AddFailure(field.Key, $"Invalid or missing {field.Type} value.");
            }
        });
    }
}

/// <summary>Publish-time validation runs asynchronously; do not use MVC's synchronous auto-validation.</summary>
public sealed class WorkflowConfigurationValidator : AbstractValidator<WorkflowSetup>
{
    public WorkflowConfigurationValidator(IApproverDirectory users)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Attributes).NotNull();
        RuleFor(x => x.Stages).NotNull().NotEmpty();
        RuleFor(x => x).CustomAsync(async (setup, context, ct) =>
        {
            if (setup.Attributes is null || setup.Stages is null || setup.Stages.Length == 0) return;
            if (setup.Attributes.Any(x => x is null) || setup.Stages.Any(x => x is null)) { context.AddFailure("Null fields/stages are not supported."); return; }
            var invalid = false;
            void Error(string message) { invalid = true; context.AddFailure(message); }
            bool Key(string key) => !string.IsNullOrEmpty(key) && System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z][A-Za-z0-9_]{0,63}$");
            if (setup.Attributes.Select(x => x.Key).Distinct().Count() != setup.Attributes.Length) Error("Duplicate attribute keys.");
            if (setup.Stages.Select(x => x.Key).Distinct().Count() != setup.Stages.Length) { Error("Duplicate stage keys."); return; }
            if (!setup.Stages.Any(x => x.Key == setup.StartStage)) Error("Unknown start stage.");
            foreach (var field in setup.Attributes)
            {
                if (field.Key is "attributes" or "requesterId" || !Key(field.Key) || !Enum.IsDefined(field.Type)) Error("Invalid attribute key/type.");
                if (field.Type == AttributeType.Choice && (field.Choices is null || field.Choices.Length == 0 || field.Choices.Any(string.IsNullOrWhiteSpace) || field.Choices.Distinct().Count() != field.Choices.Length)) Error($"Choices required for {field.Key}.");
                if (field.MaxLength is <= 0 || field.Minimum > field.Maximum) Error($"Invalid constraints for {field.Key}.");
                if (field.MaxLength is not null && field.Type != AttributeType.String) Error("MaxLength applies to strings only.");
                if ((field.Minimum is not null || field.Maximum is not null) && field.Type is not (AttributeType.Decimal or AttributeType.Integer)) Error("Numeric limits apply to numbers only.");
            }
            bool DestinationValid(Destination? destination, bool approve)
            {
                if (destination is null || !Enum.IsDefined(destination.Kind)) return false;
                if (destination.Kind is DestinationKind.Stage or DestinationKind.Correction)
                    return (!approve || destination.Kind != DestinationKind.Correction) && setup.Stages.Any(x => x.Key == destination.StageKey);
                return string.IsNullOrEmpty(destination.StageKey);
            }
            void ConditionValid(AttributeCondition? condition)
            {
                if (condition is null) { Error("A condition is required."); return; }
                var field = setup.Attributes.FirstOrDefault(x => x.Key == condition.Attribute);
                if (field is null || !Enum.IsDefined(condition.Operator)) { Error("Unknown condition field/operator."); return; }
                if (!AttributeRules.IsValid(field with { Required = true, Minimum = null, Maximum = null, MaxLength = null }, condition.Value)) Error($"Invalid condition value for {field.Key}.");
                if (condition.Operator is not (ConditionOperator.Equals or ConditionOperator.NotEquals) && field.Type is not (AttributeType.Decimal or AttributeType.Integer or AttributeType.Date)) Error("Ordered comparisons require a number or date.");
            }
            foreach (var stage in setup.Stages)
            {
                if (!Key(stage.Key) || string.IsNullOrWhiteSpace(stage.Title)) Error("A valid stage key/title is required.");
                if (!DestinationValid(stage.ApproveTo, true) || !DestinationValid(stage.RejectTo, false)) Error($"Invalid destination in {stage.Key}.");
                var assignment = stage.Assignment;
                if (assignment is null || assignment.Members is null || !Enum.IsDefined(assignment.Policy)) { Error("An assignment policy and members array are required."); continue; }
                if (assignment.Members.Distinct().Count() != assignment.Members.Length) Error("Duplicate candidate members.");
                foreach (var member in assignment.Members)
                    if (string.IsNullOrWhiteSpace(member) || !await users.HasRoleAsync(member, "Approver", ct)) Error($"Unknown/ineligible approver {member}.");
                if (assignment.Policy == AssignmentPolicy.Specific && !assignment.Members.Contains(assignment.UserId)) Error("Specific assignee must be a candidate.");
                if (assignment.Policy == AssignmentPolicy.Condition && (assignment.Rules is null || assignment.Rules.Length == 0)) Error("Conditional assignment requires ordered rules.");
                foreach (var rule in assignment.Rules ?? [])
                {
                    if (rule is null) { Error("Null assignment rule."); continue; }
                    ConditionValid(rule.When);
                    if (!assignment.Members.Contains(rule.UserId)) Error("Rule assignee must be a candidate.");
                }
                foreach (var rule in stage.ApproveRules ?? [])
                {
                    if (rule is null) { Error("Null route rule."); continue; }
                    ConditionValid(rule.When);
                    if (!DestinationValid(rule.Destination, true)) Error("Invalid conditional approval destination.");
                }
            }
            if (invalid) return;
            // Check every stage, including ones entered only after rejection/correction. Approval
            // edges must be acyclic; reject/correction edges are intentionally excluded from this walk.
            // Memoizing completed stages also avoids repeatedly traversing shared approval branches.
            var visiting = new HashSet<string>();
            var complete = new HashSet<string>();
            bool Terminates(string key)
            {
                if (complete.Contains(key)) return true;
                if (!visiting.Add(key)) return false;
                var stage = setup.Stages.Single(x => x.Key == key);
                var terminates = (stage.ApproveRules ?? []).Select(x => x.Destination).Append(stage.ApproveTo)
                    .All(destination => destination.Kind != DestinationKind.Stage || Terminates(destination.StageKey!));
                visiting.Remove(key);
                if (terminates) complete.Add(key);
                return terminates;
            }
            if (!setup.Stages.All(stage => Terminates(stage.Key))) Error("Approval cycles are not supported.");
        });
    }
}
