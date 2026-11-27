using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;

namespace IlWorkflow.Infrastructure.Workflows;

/// <summary>
/// Last node: writes the business result (Approved/Rejected) to the workflow output.
/// Elsa's own status "Finished" only says the workflow ended, not whether the ticket was approved.
/// </summary>
[Activity("Tickets", "Tickets", "Records the final business outcome of a ticket.")]
public sealed class FinishTicket : Activity
{
    [Input] public Input<string> Outcome { get; set; } = null!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var outcome = Enum.Parse<TicketOutcome>(Outcome.Get(context));
        if (outcome is not (TicketOutcome.Approved or TicketOutcome.Rejected))
            throw new InvalidOperationException("FinishTicket needs Approved or Rejected.");

        TicketState.SetOutcome(context.WorkflowExecutionContext, outcome);
        context.WorkflowExecutionContext.Output[TicketState.CurrentStage] = null!;
        await context.CompleteActivityAsync();
    }
}
