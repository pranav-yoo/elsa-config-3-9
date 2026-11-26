using FluentValidation;

namespace IlWorkflow.Application.Validation;

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
            void Error(string message) => context.AddFailure(message);
            bool Key(string key) => !string.IsNullOrEmpty(key) && System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z][A-Za-z0-9_]{0,63}$");
            if (setup.Attributes.Select(x => x.Key).Distinct().Count() != setup.Attributes.Length) Error("Duplicate attribute keys.");
            if (setup.Stages.Select(x => x.Key).Distinct().Count() != setup.Stages.Length) { Error("Duplicate stage keys."); return; }
            if (!setup.Stages.Any(x => x.Key == setup.StartStage)) Error("Unknown start stage.");
            // A parallel group means "the first approval continues, all rejections reject", so its stages offer exactly APPROVE and REJECT.
            static bool OffersApproveAndReject(StageDefinition stage) =>
                (stage.Actions ?? []).Select(x => x.Key).Order().SequenceEqual([TaskActionKeys.Approve, TaskActionKeys.Reject]);
            static Dictionary<string, Destination> DestinationsOf(StageDefinition stage) => (stage.Actions ?? []).ToDictionary(x => x.Key, x => x.Destination);
            var parallelMembers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in setup.Stages.Where(stage => stage.ParallelWith is not null))
            {
                if (entry.ParallelWith!.Length == 0) Error($"Parallel stage '{entry.Key}' must name at least one other stage.");
                if (!OffersApproveAndReject(entry)) Error($"Parallel stage '{entry.Key}' must offer exactly the {TaskActionKeys.Approve} and {TaskActionKeys.Reject} actions.");
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
                    if (!OffersApproveAndReject(member)) Error($"Parallel stage '{key}' must offer exactly the {TaskActionKeys.Approve} and {TaskActionKeys.Reject} actions.");
                    else if (OffersApproveAndReject(entry) && !DestinationsOf(member).OrderBy(x => x.Key).SequenceEqual(DestinationsOf(entry).OrderBy(x => x.Key)))
                        Error($"Parallel stage '{key}' must match the action destinations of '{entry.Key}'.");
                    if (member.Actions?.Any(x => x.Rules is { Length: > 0 }) == true) Error($"Put parallel routing rules on '{entry.Key}', not '{key}'.");
                }
            }
            if (parallelMembers.Contains(setup.StartStage)) Error("Start at the entry stage of the parallel group.");
            if (setup.Supervisors is { } supervisors)
            {
                if (supervisors.Distinct(StringComparer.Ordinal).Count() != supervisors.Length) Error("Duplicate supervisors.");
                foreach (var supervisor in supervisors)
                    if (string.IsNullOrWhiteSpace(supervisor) || !await users.HasRoleAsync(supervisor, WorkflowSetup.SupervisorRole, ct))
                        Error($"Unknown supervisor {supervisor} or missing role {WorkflowSetup.SupervisorRole}.");
            }
            var fields = new AttributeDefinitionValidator();
            foreach (var field in setup.Attributes)
                foreach (var failure in fields.Validate(field).Errors) Error(failure.ErrorMessage);
            bool DestinationValid(Destination? destination)
            {
                if (destination is null || !Enum.IsDefined(destination.Kind)) return false;
                if (destination.Kind is DestinationKind.Stage or DestinationKind.Correction)
                    return destination.StageKey is { } key && !parallelMembers.Contains(key)
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
                if (condition.OrGroup is < 1) Error("orGroup must be a positive number.");
            }
            // "conditions": entries sharing an orGroup are OR-ed; everything else is AND-ed.
            const int MaxRuleConditions = 20;
            void RuleConditionsValid(AttributeCondition[]? conditions)
            {
                if (conditions is not { Length: > 0 }) { Error("A rule needs \"conditions\" with at least one condition."); return; }
                if (conditions.Length > MaxRuleConditions) { Error($"A rule can have at most {MaxRuleConditions} conditions."); return; }
                foreach (var condition in conditions) ConditionValid(condition);
            }
            // Every stage lists its own task buttons; there are no required or built-in keys.
            void ActionsValid(StageDefinition stage)
            {
                var actions = stage.Actions;
                if (actions is not { Length: > 0 } || actions.Any(x => x is null)) { Error($"Stage '{stage.Key}' needs at least one action in \"actions\"."); return; }
                if (actions.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != actions.Length) Error($"Duplicate action keys in '{stage.Key}'.");
                foreach (var action in actions)
                {
                    if (string.IsNullOrEmpty(action.Key) || !System.Text.RegularExpressions.Regex.IsMatch(action.Key, "^[A-Z][A-Z0-9_]{0,39}$"))
                    { Error($"Action key '{action.Key}' in '{stage.Key}' must be UPPER_CASE letters, digits or underscores (max 40)."); continue; }
                    if (action.Key == TaskActionKeys.Resubmit) { Error($"'{TaskActionKeys.Resubmit}' is reserved for correction tasks."); continue; }
                    if (!DestinationValid(action.Destination)) Error($"Invalid destination for action '{action.Key}' in '{stage.Key}'.");
                    foreach (var rule in action.Rules ?? [])
                    {
                        if (rule is null) { Error("Null route rule."); continue; }
                        RuleConditionsValid(rule.Conditions);
                        if (!DestinationValid(rule.Destination)) Error($"Invalid conditional destination for action '{action.Key}' in '{stage.Key}'.");
                    }
                }
            }
            foreach (var stage in setup.Stages)
            {
                if (!Key(stage.Key) || string.IsNullOrWhiteSpace(stage.Title)) Error("A valid stage key/title is required.");
                var viewerStages = new HashSet<string>(StringComparer.Ordinal);
                foreach (var key in stage.ViewableByStages ?? [])
                    if (string.IsNullOrWhiteSpace(key) || key == stage.Key || !viewerStages.Add(key) || !setup.Stages.Any(other => other.Key == key))
                        Error($"Invalid viewer stage '{key}' on '{stage.Key}'. Use distinct keys of other stages.");
                ActionsValid(stage);
                var assignment = stage.Assignment;
                if (assignment is null || assignment.Members is null || !Enum.IsDefined(assignment.Policy)) { Error("An assignment policy and members array are required."); continue; }
                if (assignment.Members.Distinct().Count() != assignment.Members.Length) Error("Duplicate candidate members.");
                foreach (var member in assignment.Members)
                    if (string.IsNullOrWhiteSpace(member) || !await users.HasRoleAsync(member, "Approver", ct)) Error($"Unknown/ineligible approver {member}.");
                if (assignment.Policy == AssignmentPolicy.Specific)
                {
                    // userId may be omitted when there is exactly one member; that member is the owner.
                    if (assignment.SpecificOwner is null) Error($"Specific assignment in '{stage.Key}' needs userId when it has more than one member.");
                    else if (!assignment.Members.Contains(assignment.SpecificOwner)) Error("Specific assignee must be a candidate.");
                }
                if (assignment.Policy == AssignmentPolicy.Condition && (assignment.Rules is null || assignment.Rules.Length == 0)) Error("Conditional assignment requires ordered rules.");
                foreach (var rule in assignment.Rules ?? [])
                {
                    if (rule is null) { Error("Null assignment rule."); continue; }
                    RuleConditionsValid(rule.Conditions);
                    if (!assignment.Members.Contains(rule.UserId)) Error("Rule assignee must be a candidate.");
                }
            }
        });
    }
}
