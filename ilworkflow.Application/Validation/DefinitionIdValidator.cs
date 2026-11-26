using FluentValidation;

namespace IlWorkflow.Application.Validation;

public sealed class DefinitionIdValidator : AbstractValidator<string>
{
    public DefinitionIdValidator() => RuleFor(x => x).NotEmpty().Matches("^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$");
}
