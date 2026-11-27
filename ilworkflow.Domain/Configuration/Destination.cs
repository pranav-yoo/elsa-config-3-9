namespace IlWorkflow.Domain.Configuration;

/// <summary>
/// Where a ticket goes after a button is pressed.
/// <list type="bullet">
/// <item><c>Stage</c> + StageKey: open that stage's task.</item>
/// <item><c>Correction</c> + StageKey: send the ticket back to the requester; after RESUBMIT it goes to StageKey.</item>
/// <item><c>Approved</c> / <c>Rejected</c>: finish the ticket with that outcome.</item>
/// </list>
/// </summary>
public sealed record Destination(DestinationKind Kind, string? StageKey = null);
