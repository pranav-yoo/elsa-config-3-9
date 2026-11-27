using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Expressions.Models;
using Elsa.Extensions;
using Elsa.UserTasks.Activities;
using Elsa.UserTasks.Models;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using UserTask = Elsa.UserTasks.Activities.UserTask;

namespace IlWorkflow.Infrastructure.Workflows;

/// <summary>
/// One stage visit: creates a native Elsa <see cref="UserTask"/> and waits until a user completes it.
///
/// How it works:
/// 1. <see cref="ExecuteAsync"/> decides who gets the task and saves that on this activity's context.
/// 2. It schedules the child <see cref="Review"/> user task. The workflow now SUSPENDS and is saved to the database.
/// 3. When a user completes the task, Elsa resumes the workflow and calls <see cref="OnReviewCompletedAsync"/>.
/// 4. The activity finishes with an outcome named after the pressed button (for example "APPROVE"),
///    and the flowchart follows the connection with that name.
///
/// The same activity is used for correction tasks (Correction = true): the requester gets a RESUBMIT button.
/// </summary>
[Activity("Tickets", "Tickets", "Creates one approval or correction task and waits for the decision.")]
[FlowNode(StagePorts.Resubmit)]
public sealed class TicketReview : Activity
{
    public const string TaskType = "configured-ticket";

    // Values saved on this activity's context in ExecuteAsync and read back by the user task (keys of the property bag).
    private const string SetupKey = "schema";
    private const string StageKeyKey = "stageKey";
    private const string AssignmentKey = "assignment";
    private const string CorrectionKey = "correction";
    private const string ParallelBranchKey = "parallelBranch";
    private const string TitleKey = "title";
    private const string ObserverTagsKey = "observerTags";

    /// <summary>The setup snapshot this workflow version was built from (never the latest editable draft).</summary>
    [Input] public Input<string> ConfigurationJson { get; set; } = null!;
    [Input] public Input<string> StageKey { get; set; } = null!;
    /// <summary>True for a "correct and resubmit" task for the requester.</summary>
    [Input] public Input<bool> Correction { get; set; } = new(false);
    /// <summary>True when this task is one branch of a <see cref="ParallelTicketReview"/>.</summary>
    [Input] public Input<bool> ParallelBranch { get; set; } = new(false);

    [JsonIgnore, Port("Review")] public UserTask Review { get; set; }

    public TicketReview()
    {
        // These lambdas run when Elsa creates the task. "Owner(ctx)" is this TicketReview's context,
        // where ExecuteAsync stored the values.
        Review = new UserTask
        {
            Id = "review",
            TaskType = new(ctx => Owner(ctx).GetProperty<bool>(ParallelBranchKey) ? ParallelTicketReview.TaskType : TaskType),
            Title = new(ctx => Owner(ctx).GetProperty<string>(TitleKey)!),
            Summary = new(ctx => GetAssignment(Owner(ctx)).Blocked ? "Assignment blocked. Supervisor reassignment required." : "Review this request."),
            Reference = new(ctx => Owner(ctx).WorkflowExecutionContext.CorrelationId),
            Requester = new(ctx => TicketState.User(TicketState.GetRequester(Owner(ctx).WorkflowExecutionContext))),
            CandidateUsers = new(ctx => GetAssignment(Owner(ctx)).Candidates.Select(TicketState.User).ToArray()),
            Assignee = new(ctx => GetAssignment(Owner(ctx)).Assignee is { } id ? TicketState.User(id) : null),
            // Snapshot: keep the candidate list as it was when the task was created.
            MembershipResolutionMode = new(UserTaskMembershipResolutionMode.Snapshot),
            Tags = new(ctx => Application.Common.ConfigurationJson.Read<string[]>(Owner(ctx).GetProperty<string>(ObserverTagsKey)!)),
            TaskData = new(ctx => TicketState.GetAttributes(Owner(ctx).WorkflowExecutionContext)),
            ActionDefinitions = new(ctx => GetButtons(Owner(ctx))),
            // TicketFormProvider validates the completion data. The key tells it which stage's rules apply.
            Form = new(ctx => new UserTaskFormReference(TicketFormProvider.ProviderName, FormKey(Owner(ctx)), Owner(ctx).GetProperty<string>(SetupKey)))
        };
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var setupJson = ConfigurationJson.Get(context);
        var setup = Application.Common.ConfigurationJson.Read<WorkflowSetup>(setupJson);
        var stage = setup.FindStage(StageKey.Get(context)) ?? throw new InvalidOperationException($"Unknown stage '{StageKey.Get(context)}'.");
        var correction = Correction.Get(context);
        var parallelBranch = ParallelBranch.Get(context);
        var workflow = context.WorkflowExecutionContext;

        // A correction always goes back to the requester. A review goes to the stage's members.
        var requester = TicketState.GetRequester(workflow);
        var assignment = correction
            ? new ResolvedAssignment([requester], requester, false)
            : await context.GetRequiredService<AssignmentResolver>().ResolveAsync(stage.Assignment, context.CancellationToken);

        context.SetProperty(SetupKey, setupJson);
        context.SetProperty(StageKeyKey, stage.Key);
        context.SetProperty(AssignmentKey, Application.Common.ConfigurationJson.Write(assignment));
        context.SetProperty(CorrectionKey, correction);
        context.SetProperty(ParallelBranchKey, parallelBranch);
        context.SetProperty(TitleKey, correction ? "Correct and resubmit: " + stage.Title : stage.Title);
        // Stored as JSON text: the property bag keeps plain values most reliably.
        context.SetProperty(ObserverTagsKey, Application.Common.ConfigurationJson.Write(await ObserverTagsAsync(context, setup, stage, correction)));

        // Parallel branches share one stage entry; ParallelTicketReview updates the stage for the whole group.
        if (!parallelBranch)
        {
            workflow.Output[TicketState.CurrentStage] = stage.Key;
            TicketState.SetOutcome(workflow, correction ? TicketOutcome.Returned : TicketOutcome.InReview);
        }

        await context.ScheduleActivityAsync(Review, OnReviewCompletedAsync);
    }

    private async ValueTask OnReviewCompletedAsync(ActivityCompletedContext completed)
    {
        var context = completed.TargetContext;
        var workflow = context.WorkflowExecutionContext;
        var stimulus = context.GetWorkflowInput<UserTaskStimulus>(UserTask.InputKey);

        if (context.GetProperty<bool>(CorrectionKey))
        {
            // TicketFormProvider has already checked the data shape.
            var attributes = stimulus.CompletionData!.Value.GetProperty(TicketState.Attributes).Clone();
            TicketState.AddAttributeRevision(workflow, attributes);
            await context.CompleteActivityWithOutcomesAsync(StagePorts.Resubmit);
            return;
        }

        // Use the key exactly as configured, whatever casing the client sent.
        var action = GetStage(context).Actions!.First(x => string.Equals(x.Key, stimulus.ActionKey, StringComparison.OrdinalIgnoreCase));
        if (!context.GetProperty<bool>(ParallelBranchKey))
            workflow.Output[TicketState.LastDecision] = action.Key;
        await context.CompleteActivityWithOutcomesAsync(action.Key);
    }

    /// <summary>Members of the stages in ViewableByStages may read this task. Elsa stores that as task tags.</summary>
    private static async Task<string[]> ObserverTagsAsync(ActivityExecutionContext context, WorkflowSetup setup, StageDefinition stage, bool correction)
    {
        var users = context.GetRequiredService<IApproverDirectory>();
        var tags = new List<string>();

        if (!correction)
        {
            var viewerStages = stage.ViewableByStages ?? [];
            var viewers = setup.Stages.Where(other => viewerStages.Contains(other.Key)).SelectMany(other => other.Assignment.Members).Distinct();
            foreach (var viewer in viewers)
                if (await users.HasRoleAsync(viewer, WorkflowConfigurationValidator.ApproverRole, context.CancellationToken))
                    tags.Add(ObservedTaskAccess.TagFor(TicketState.User(viewer)));
        }

        // Workflow supervisors see every task of this workflow, including corrections.
        foreach (var supervisor in setup.Supervisors ?? [])
            if (await users.HasRoleAsync(supervisor, WorkflowSetup.SupervisorRole, context.CancellationToken))
                tags.Add(ObservedTaskAccess.SupervisorTagFor(TicketState.User(supervisor)));

        return [.. tags];
    }

    private static ActivityExecutionContext Owner(ExpressionExecutionContext context) =>
        context.GetActivityExecutionContext().ParentActivityExecutionContext!;

    private static ResolvedAssignment GetAssignment(ActivityExecutionContext owner) =>
        Application.Common.ConfigurationJson.Read<ResolvedAssignment>(owner.GetProperty<string>(AssignmentKey)!);

    private static StageDefinition GetStage(ActivityExecutionContext owner) =>
        Application.Common.ConfigurationJson.Read<WorkflowSetup>(owner.GetProperty<string>(SetupKey)!).FindStage(owner.GetProperty<string>(StageKeyKey)!)!;

    private static UserTaskActionDefinition[] GetButtons(ActivityExecutionContext owner) =>
        owner.GetProperty<bool>(CorrectionKey)
            ? [new(TaskActionKeys.Resubmit, "Resubmit")]
            : GetStage(owner).Actions!.Select(action => new UserTaskActionDefinition(action.Key, action.DisplayLabel)).ToArray();

    private static string FormKey(ActivityExecutionContext owner) =>
        owner.GetProperty<bool>(CorrectionKey) ? TicketFormProvider.CorrectionFormKey : TicketFormProvider.ReviewFormKey(owner.GetProperty<string>(StageKeyKey)!);
}
