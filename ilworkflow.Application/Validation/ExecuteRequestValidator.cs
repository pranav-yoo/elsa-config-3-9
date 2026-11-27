using System.Text.Json.Nodes;
using FluentValidation;

namespace IlWorkflow.Application.Validation;

/// <summary>
/// Body of POST /elsa/api/workflow-definitions/{id}/execute for a configured workflow:
/// <c>{ "correlationId": "...", "input": { "attributes": { ... } } }</c>.
/// The attributes are the ticket's business data; this API stores them but does not interpret them.
/// </summary>
public sealed class ExecuteRequestValidator : AbstractValidator<JsonObject>
{
    private static readonly string[] AllowedBodyFields = ["input", "correlationId", "name", "versionOptions"];
    private static readonly string[] AllowedInputFields = ["attributes", "requesterId"];

    public ExecuteRequestValidator()
    {
        RuleFor(body => body).Must(body => body.All(field => AllowedBodyFields.Contains(field.Key)))
            .WithMessage("Only input, correlationId, name and versionOptions are accepted.");
        RuleFor(body => body["input"]).Must(input => input is null or JsonObject)
            .WithMessage("input must be an object.");
        RuleFor(body => body["input"] as JsonObject).Must(input => input!.All(field => AllowedInputFields.Contains(field.Key)))
            .When(body => body["input"] is JsonObject).WithMessage("Supply ticket fields inside input.attributes.");
        RuleFor(body => body["input"]!["attributes"]).Must(attributes => attributes is null or JsonObject)
            .When(body => body["input"] is JsonObject).WithMessage("input.attributes must be an object.");
    }
}
