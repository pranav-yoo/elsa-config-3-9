namespace IlWorkflow.Contracts.WorkflowConfigurations.Model;

/// <summary>
/// Action keys that have a special meaning.
/// RESUBMIT is the only button on a correction task. APPROVE and REJECT are required on parallel stages.
/// Every other stage is free to define its own keys (for example REFER_UW).
/// </summary>
public static class TaskActionKeys
{
    public const string Approve = "APPROVE";
    public const string Reject = "REJECT";
    public const string Resubmit = "RESUBMIT";
}
