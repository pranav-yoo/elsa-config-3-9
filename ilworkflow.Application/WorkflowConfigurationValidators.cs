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
            var parallelMembers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in setup.Stages.Where(stage => stage.ParallelWith is not null))
            {
                if (entry.ParallelWith!.Length == 0) Error($"Parallel stage '{entry.Key}' must name at least one other stage.");
                foreach (var key in entry.ParallelWith)
                {
                    var member = setup.Stages.FirstOrDefault(stage => stage.Key == key);
                    if (string.IsNullOrEmpty(key) || member is null || member == entry)
                    {
                        Error($"Invalid parallel stage '{key}' in '{entry.Key}'.");
                        continue;
                    }
                    if (!parallelMembers.Add(key)) Error($"Parallel stage '{key}' is listed more than once.");
                    if (member.ParallelWith is not null) Error($"Parallel stage '{key}' cannot start another group.");
                    if (member.ApproveTo != entry.ApproveTo || member.RejectTo != entry.RejectTo)
                        Error($"Parallel stage '{key}' must match the approval and rejection destinations of '{entry.Key}'.");
                    if (member.ApproveRules is { Length: > 0 }) Error($"Put parallel approval rules on '{entry.Key}', not '{key}'.");
                }
            }
            if (parallelMembers.Contains(setup.StartStage)) Error("Start at the entry stage of the parallel group.");
            var fields = new AttributeDefinitionValidator();
            foreach (var field in setup.Attributes)
                foreach (var failure in fields.Validate(field).Errors) Error(failure.ErrorMessage);
            bool DestinationValid(Destination? destination, bool approve)
            {
                if (destination is null || !Enum.IsDefined(destination.Kind)) return false;
                if (destination.Kind is DestinationKind.Stage or DestinationKind.Correction)
                    return (!approve || destination.Kind != DestinationKind.Correction)
                        && destination.StageKey is { } key && !parallelMembers.Contains(key)
                        && setup.Stages.Any(x => x.Key == key);
                return string.IsNullOrEmpty(destination.StageKey);
            }
            void ConditionValid(AttributeCondition? condition)
            {
                if (condition is null) { Error("A condition is required."); return; }
                var field = setup.Attributes.FirstOrDefault(x => x.Key == condition.Attribute);
                if (field is null) { Error($"Condition attribute '{condition.Attribute}' is missing or inactive for this definition ID."); return; }
                if (!Enum.IsDefined(condition.Operator)) { Error("Unknown condition operator."); return; }
                if (!AttributeRules.IsValid(field with { Required = true, Minimum = null, Maximum = null, MaxLength = null }, condition.Value)) Error($"Invalid condition value for {field.Key}.");
                if (condition.Operator is not (ConditionOperator.Equals or ConditionOperator.NotEquals) && field.Type is not (AttributeType.Decimal or AttributeType.Integer or AttributeType.Date)) Error("Ordered comparisons require a number or date.");
            }
            foreach (var stage in setup.Stages)
            {
                if (!Key(stage.Key) || string.IsNullOrWhiteSpace(stage.Title)) Error("A valid stage key/title is required.");
                var viewerStages = new HashSet<string>(StringComparer.Ordinal);
                foreach (var key in stage.ViewableByStages ?? [])
                    if (string.IsNullOrWhiteSpace(key) || key == stage.Key || !viewerStages.Add(key) || !setup.Stages.Any(other => other.Key == key))
                        Error($"Invalid viewer stage '{key}' on '{stage.Key}'. Use distinct keys of other stages.");
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
            if (!setup.Stages.Where(stage => !parallelMembers.Contains(stage.Key)).All(stage => Terminates(stage.Key)))
                Error("Approval cycles are not supported.");
        });
    }
}
