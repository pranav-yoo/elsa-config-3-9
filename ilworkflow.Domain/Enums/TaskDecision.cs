namespace IlWorkflow.Domain.Enums;

// Custom = a configured stage action (e.g. REFER_UW); the graph routes it by the recorded action key.
public enum TaskDecision { Approve, Reject, Resubmit, Custom }
