using System.Text.Json;
using FluentValidation;

namespace IlWorkflow.Application.Validation;

public sealed class TicketInputValidator : AbstractValidator<Dictionary<string, JsonElement>>
{
    public TicketInputValidator(WorkflowSetup setup)
    {
        RuleFor(x => x).Custom((values, context) =>
        {
            foreach (var key in values.Keys.Where(key => !setup.Attributes.Any(field => field.Key == key)))
                context.AddFailure(key, "Unknown attribute.");
            foreach (var field in setup.Attributes)
            {
                values.TryGetValue(field.Key, out var value);
                if (!AttributeRules.IsValid(field, value)) context.AddFailure(field.Key, $"Invalid or missing {field.Type} value.");
            }
        });
    }
}
