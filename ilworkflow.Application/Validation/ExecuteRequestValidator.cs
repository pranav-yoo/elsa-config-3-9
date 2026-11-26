using System.Text.Json.Nodes;
using FluentValidation;

namespace IlWorkflow.Application.Validation;

/// <summary>Shape of the body of POST workflow-definitions/{id}/execute for a configured workflow.</summary>
public sealed class ExecuteRequestValidator : AbstractValidator<JsonObject>
{
    public ExecuteRequestValidator()
    {
        RuleFor(x => x).Must(body => body.All(property => property.Key is "input" or "correlationId" or "name" or "versionOptions"))
            .WithMessage("Custom variables and alternate activity starts are not permitted for configured tickets.");
        RuleFor(x => x).Must(body => body["input"] is not JsonObject input || input.All(property => property.Key is "attributes" or "requesterId"))
            .WithMessage("Supply ticket fields inside input.attributes.");
    }
}
