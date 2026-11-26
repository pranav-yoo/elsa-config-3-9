namespace IlWorkflow.Api.Workflows;

/// <summary>Outcome ports of the generated flowchart. Action: any configured stage action (routed by the recorded key). Resubmit: a correction task.</summary>
public static class StagePorts
{
    public const string Action = "Action";
    public const string Resubmit = "Resubmit";
    // Parallel groups only: the first approval continues, all rejections reject.
    public const string Approve = "Approve";
    public const string Reject = "Reject";
}
