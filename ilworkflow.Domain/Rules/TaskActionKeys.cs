namespace IlWorkflow.Domain.Rules;

/// <summary>
/// Action keys with a fixed meaning. RESUBMIT is reserved for correction tasks. APPROVE and REJECT matter only inside a
/// parallel group ("first approval continues, all rejections reject"); every other stage defines its own action keys.
/// </summary>
public static class TaskActionKeys
{
    public const string Approve = "APPROVE";
    public const string Reject = "REJECT";
    public const string Resubmit = "RESUBMIT";
}
