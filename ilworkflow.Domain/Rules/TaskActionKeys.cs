using IlWorkflow.Domain.Enums;

namespace IlWorkflow.Domain.Rules;

public static class TaskActionKeys
{
    public const string Approve = "APPROVE";
    public const string Reject = "REJECT";
    public const string Resubmit = "RESUBMIT";

    // Pending Elsa tasks retain the key casing recorded when they were created.
    public static TaskDecision? Parse(string? key) => key?.ToUpperInvariant() switch
    {
        Approve => TaskDecision.Approve,
        Reject => TaskDecision.Reject,
        Resubmit => TaskDecision.Resubmit,
        _ => null
    };
}
