using Elsa.Extensions;
using Elsa.UserTasks.Models;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using UserTask = Elsa.UserTasks.Activities.UserTask;

namespace IlWorkflow.Api.Workflows;

[Activity("Tickets", "Tickets", "Runs parallel reviews and continues on the first approval.")]
[FlowNode(nameof(TaskDecision.Approve), nameof(TaskDecision.Reject))]
public sealed class ParallelTicketReview : Activity
{
    public const string TaskType = "configured-parallel-ticket";

    [Input] public Input<string> StageKey { get; set; } = null!;
    public ICollection<IActivity> Branches { get; set; } = [];

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        context.WorkflowExecutionContext.Output["currentStage"] = StageKey.Get(context);
        context.WorkflowExecutionContext.Output["ticketOutcome"] = TicketOutcome.InReview.ToString();
        await context.ScheduleActivities(Branches, OnBranchCompletedAsync);
    }

    private async ValueTask OnBranchCompletedAsync(ActivityCompletedContext completed)
    {
        var context = completed.TargetContext;
        var stimulus = context.GetWorkflowInput<UserTaskStimulus>(UserTask.InputKey);
        var decision = TaskActionKeys.Parse(stimulus.ActionKey);
        if (decision == TaskDecision.Reject)
        {
            // This activity context is persisted per visit, including across rejection and restart.
            var rejected = context.GetProperty<int>("rejectedBranches") + 1;
            context.SetProperty("rejectedBranches", rejected);
            if (rejected < Branches.Count) return;
        }
        else if (decision != TaskDecision.Approve)
            throw new InvalidOperationException("Unknown parallel review decision.");

        var outcome = decision.Value.ToString();
        context.WorkflowExecutionContext.Output["lastDecision"] = outcome;
        // Completing a composite cancels its remaining active children and removes their bookmarks.
        await context.CompleteActivityWithOutcomesAsync(outcome);
    }
}
