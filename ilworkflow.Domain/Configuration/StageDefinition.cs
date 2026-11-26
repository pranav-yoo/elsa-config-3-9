using System.Text.Json.Serialization;

namespace IlWorkflow.Domain.Configuration;

/// <param name="Actions">The task buttons of this stage; each one carries its own destination, optional rules and reason flag.</param>
public sealed record StageDefinition(string Key, string Title, AssignmentDefinition Assignment,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? ParallelWith = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? ViewableByStages = null,
    StageAction[]? Actions = null);
