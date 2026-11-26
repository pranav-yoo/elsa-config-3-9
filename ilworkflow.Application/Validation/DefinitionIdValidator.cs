using FluentValidation;

namespace IlWorkflow.Application.Validation;

public sealed class DefinitionIdValidator : AbstractValidator<string>
{
    public DefinitionIdValidator() => RuleFor(x => x).NotEmpty().Matches("^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$")
            .WithMessage("workflow_definition_id must start with a letter or digit and use only letters, digits, '_' or '-' (max 128 characters).");
}
