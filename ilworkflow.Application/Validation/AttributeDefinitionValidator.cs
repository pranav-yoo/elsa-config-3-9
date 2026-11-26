using FluentValidation;

namespace IlWorkflow.Application.Validation;

public sealed class AttributeDefinitionValidator : AbstractValidator<AttributeDefinition>
{
    public AttributeDefinitionValidator()
    {
        RuleFor(x => x.Key).NotEmpty().Matches("^[A-Za-z][A-Za-z0-9_]{0,63}$")
            .Must(key => key is not ("attributes" or "requesterId"));
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x).Custom((field, context) =>
        {
            if (field.Type == AttributeType.Choice && (field.Choices is null || field.Choices.Length == 0 || field.Choices.Any(string.IsNullOrWhiteSpace) || field.Choices.Distinct().Count() != field.Choices.Length)) context.AddFailure("Choices must be nonempty and unique.");
            if (field.MaxLength is <= 0 || field.Minimum > field.Maximum) context.AddFailure("Invalid field constraints.");
            if (field.MaxLength is not null && field.Type != AttributeType.String) context.AddFailure("MaxLength applies to strings only.");
            if ((field.Minimum is not null || field.Maximum is not null) && field.Type is not (AttributeType.Decimal or AttributeType.Integer)) context.AddFailure("Numeric limits apply to numbers only.");
        });
    }
}
