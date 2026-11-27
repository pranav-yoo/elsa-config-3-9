namespace IlWorkflow.Infrastructure.Workflows;

/// <summary>
/// Names of the outcome ports used in the generated flowchart.
/// A normal review task leaves through a port named after the pressed action key (APPROVE, REFER_UW, ...).
/// </summary>
public static class StagePorts
{
    /// <summary>A correction task was resubmitted by the requester.</summary>
    public const string Resubmit = "Resubmit";

    // Parallel groups only: the first approval continues, all rejections reject.
    public const string Approve = "Approve";
    public const string Reject = "Reject";
}
