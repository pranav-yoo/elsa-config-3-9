using Elsa.Extensions;
using Elsa.UserTasks.Models;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using UserTask = Elsa.UserTasks.Activities.UserTask;

namespace IlWorkflow.Infrastructure.Workflows;

/// <summary>
/// A parallel group: opens one <see cref="TicketReview"/> task per stage at the same time.
/// The first APPROVE wins (the other open tasks are cancelled). The group is rejected only when every task rejects.
/// </summary>
[Activity("Tickets", "Tickets", "Runs parallel reviews and continues on the first approval.")]
[FlowNode(StagePorts.Approve, StagePorts.Reject)]
public sealed class ParallelTicketReview : Activity
{
    public const string TaskType = "configured-parallel-ticket";

    [Input] public Input<string> StageKey { get; set; } = null!;
    public ICollection<IActivity> Branches { get; set; } = [];

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        context.WorkflowExecutionContext.Output[TicketState.CurrentStage] = StageKey.Get(context);
        TicketState.SetOutcome(context.WorkflowExecutionContext, TicketOutcome.InReview);
        await context.ScheduleActivities(Branches, OnBranchCompletedAsync);
    }

    private async ValueTask OnBranchCompletedAsync(ActivityCompletedContext completed)
    {
        var context = completed.TargetContext;
        var stimulus = context.GetWorkflowInput<UserTaskStimulus>(UserTask.InputKey);
        var rejected = string.Equals(stimulus.ActionKey, TaskActionKeys.Reject, StringComparison.OrdinalIgnoreCase);
        if (rejected)
        {
            // This activity context is persisted per visit, including across rejection and restart.
            var rejectedBranches = context.GetProperty<int>("rejectedBranches") + 1;
            context.SetProperty("rejectedBranches", rejectedBranches);
            if (rejectedBranches < Branches.Count) return;
        }
        else if (!string.Equals(stimulus.ActionKey, TaskActionKeys.Approve, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unknown parallel review decision.");

        context.WorkflowExecutionContext.Output[TicketState.LastDecision] = rejected ? TaskActionKeys.Reject : TaskActionKeys.Approve;
        // Completing a composite cancels its remaining active children and removes their bookmarks.
        await context.CompleteActivityWithOutcomesAsync(rejected ? StagePorts.Reject : StagePorts.Approve);
    }
}
