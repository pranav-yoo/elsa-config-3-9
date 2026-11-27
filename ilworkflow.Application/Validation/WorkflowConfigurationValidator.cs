using System.Text.RegularExpressions;
using FluentValidation;

namespace IlWorkflow.Application.Validation;

/// <summary>
/// Checks that a workflow setup is complete and consistent before it is saved or published.
/// Each rule below is a small method with a clear name; read them top to bottom.
/// </summary>
public sealed partial class WorkflowConfigurationValidator : AbstractValidator<WorkflowSetup>
{
    public const string ApproverRole = "Approver";

    private readonly IApproverDirectory _users;

    public WorkflowConfigurationValidator(IApproverDirectory users)
    {
        _users = users;

        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Stages).NotEmpty().WithMessage("At least one stage is required.");
        RuleForEach(x => x.Stages).NotNull().WithMessage("Stages cannot be null.");

        // The rules below compare stages with each other, so they only run on a basic valid list.
        When(HasUsableStages, () =>
        {
            RuleFor(x => x).Custom(CheckStageKeys);
            RuleFor(x => x).Custom(CheckActions);
            RuleFor(x => x).Custom(CheckParallelGroups);
            RuleFor(x => x).Custom(CheckViewers);
            RuleFor(x => x).CustomAsync(CheckAssignmentsAsync);
            RuleFor(x => x).CustomAsync(CheckSupervisorsAsync);
        });
    }

    private static bool HasUsableStages(WorkflowSetup setup) =>
        setup.Stages is { Length: > 0 } && setup.Stages.All(stage => stage is not null);

    private static void CheckStageKeys(WorkflowSetup setup, ValidationContext<WorkflowSetup> context)
    {
        foreach (var stage in setup.Stages)
        {
            if (stage.Key is null || !StageKeyPattern().IsMatch(stage.Key))
                context.AddFailure($"Stage key '{stage.Key}' must start with a letter and use letters, digits or '_' (max 64).");
            if (string.IsNullOrWhiteSpace(stage.Title))
                context.AddFailure($"Stage '{stage.Key}' needs a title.");
        }

        var duplicates = setup.Stages.GroupBy(stage => stage.Key).Where(group => group.Count() > 1).Select(group => group.Key);
        foreach (var key in duplicates)
            context.AddFailure($"Stage key '{key}' is used more than once.");

        if (setup.FindStage(setup.StartStage) is null)
            context.AddFailure($"Start stage '{setup.StartStage}' does not exist.");
    }

    private static void CheckActions(WorkflowSetup setup, ValidationContext<WorkflowSetup> context)
    {
        foreach (var stage in setup.Stages)
        {
            if (stage.Actions is not { Length: > 0 } || stage.Actions.Any(action => action is null))
            {
                context.AddFailure($"Stage '{stage.Key}' needs at least one action in \"actions\".");
                continue;
            }

            if (stage.Actions.DistinctBy(action => action.Key, StringComparer.OrdinalIgnoreCase).Count() != stage.Actions.Length)
                context.AddFailure($"Stage '{stage.Key}' has duplicate action keys.");

            foreach (var action in stage.Actions)
            {
                if (action.Key is null || !ActionKeyPattern().IsMatch(action.Key))
                    context.AddFailure($"Action key '{action.Key}' in '{stage.Key}' must be UPPER_CASE letters, digits or '_' (max 40).");
                else if (action.Key == TaskActionKeys.Resubmit)
                    context.AddFailure($"'{TaskActionKeys.Resubmit}' is reserved for correction tasks.");

                if (!IsValidDestination(setup, action.Destination))
                    context.AddFailure($"Action '{action.Key}' in '{stage.Key}' has an invalid destination.");
            }
        }
    }

    /// <summary>Stage and Correction need an existing stage that is not inside a parallel group; Approved/Rejected need none.</summary>
    private static bool IsValidDestination(WorkflowSetup setup, Destination? destination)
    {
        if (destination is null || !Enum.IsDefined(destination.Kind)) return false;

        if (destination.Kind is DestinationKind.Approved or DestinationKind.Rejected)
            return string.IsNullOrEmpty(destination.StageKey);

        var target = destination.StageKey is null ? null : setup.FindStage(destination.StageKey);
        return target is not null && !ParallelMemberKeys(setup).Contains(target.Key);
    }

    /// <summary>
    /// A parallel group is one "entry" stage (with ParallelWith) plus its member stages. All of them open together,
    /// the first APPROVE continues and the group is rejected only when every task rejects.
    /// </summary>
    private static void CheckParallelGroups(WorkflowSetup setup, ValidationContext<WorkflowSetup> context)
    {
        var seenMembers = new HashSet<string>();
        foreach (var entry in setup.Stages.Where(stage => stage.IsParallelGroup))
        {
            if (entry.ParallelWith!.Length == 0)
                context.AddFailure($"Parallel stage '{entry.Key}' must name at least one other stage.");
            if (!OffersOnlyApproveAndReject(entry))
                context.AddFailure($"Parallel stage '{entry.Key}' must offer exactly the APPROVE and REJECT actions.");

            foreach (var memberKey in entry.ParallelWith)
            {
                var member = setup.FindStage(memberKey);
                if (member is null || member == entry)
                {
                    context.AddFailure($"Parallel stage '{memberKey}' in '{entry.Key}' does not exist.");
                    continue;
                }
                if (!seenMembers.Add(memberKey))
                    context.AddFailure($"Stage '{memberKey}' is in more than one parallel group.");
                if (member.IsParallelGroup)
                    context.AddFailure($"Stage '{memberKey}' is a member and cannot start its own group.");
                if (!OffersOnlyApproveAndReject(member))
                    context.AddFailure($"Parallel stage '{memberKey}' must offer exactly the APPROVE and REJECT actions.");
                else if (!SameDestinations(entry, member))
                    context.AddFailure($"Parallel stage '{memberKey}' must use the same destinations as '{entry.Key}'.");
            }
        }

        if (seenMembers.Contains(setup.StartStage))
            context.AddFailure("Start at the entry stage of a parallel group, not at a member.");
    }

    private static bool OffersOnlyApproveAndReject(StageDefinition stage) =>
        (stage.Actions ?? []).Select(action => action.Key).Order().SequenceEqual([TaskActionKeys.Approve, TaskActionKeys.Reject]);

    private static bool SameDestinations(StageDefinition a, StageDefinition b) =>
        a.Actions!.All(action => b.Actions!.Single(other => other.Key == action.Key).Destination == action.Destination);

    private static HashSet<string> ParallelMemberKeys(WorkflowSetup setup) =>
        setup.Stages.SelectMany(stage => stage.ParallelWith ?? []).ToHashSet();

    private static void CheckViewers(WorkflowSetup setup, ValidationContext<WorkflowSetup> context)
    {
        foreach (var stage in setup.Stages)
        {
            var viewers = stage.ViewableByStages ?? [];
            if (viewers.Distinct().Count() != viewers.Length)
                context.AddFailure($"Stage '{stage.Key}' lists a viewer stage more than once.");
            foreach (var key in viewers)
                if (key == stage.Key || setup.FindStage(key) is null)
                    context.AddFailure($"Viewer stage '{key}' on '{stage.Key}' must be another existing stage.");
        }
    }

    private async Task CheckAssignmentsAsync(WorkflowSetup setup, ValidationContext<WorkflowSetup> context, CancellationToken ct)
    {
        foreach (var stage in setup.Stages)
        {
            var assignment = stage.Assignment;
            if (assignment?.Members is null || !Enum.IsDefined(assignment.Policy))
            {
                context.AddFailure($"Stage '{stage.Key}' needs an assignment policy and a members array.");
                continue;
            }

            if (assignment.Members.Distinct().Count() != assignment.Members.Length)
                context.AddFailure($"Stage '{stage.Key}' lists a member more than once.");

            foreach (var member in assignment.Members)
                if (string.IsNullOrWhiteSpace(member) || !await _users.HasRoleAsync(member, ApproverRole, ct))
                    context.AddFailure($"Member '{member}' of '{stage.Key}' is unknown or lacks the {ApproverRole} role.");

            if (assignment.Policy == AssignmentPolicy.Specific)
            {
                if (assignment.SpecificOwner is null)
                    context.AddFailure($"Specific assignment in '{stage.Key}' needs user_id when it has more than one member.");
                else if (!assignment.Members.Contains(assignment.SpecificOwner))
                    context.AddFailure($"user_id of '{stage.Key}' must be one of its members.");
            }
        }
    }

    private async Task CheckSupervisorsAsync(WorkflowSetup setup, ValidationContext<WorkflowSetup> context, CancellationToken ct)
    {
        var supervisors = setup.Supervisors ?? [];
        if (supervisors.Distinct().Count() != supervisors.Length)
            context.AddFailure("A supervisor is listed more than once.");
        foreach (var supervisor in supervisors)
            if (string.IsNullOrWhiteSpace(supervisor) || !await _users.HasRoleAsync(supervisor, WorkflowSetup.SupervisorRole, ct))
                context.AddFailure($"Supervisor '{supervisor}' is unknown or lacks the {WorkflowSetup.SupervisorRole} role.");
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex StageKeyPattern();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,39}$")]
    private static partial Regex ActionKeyPattern();
}
