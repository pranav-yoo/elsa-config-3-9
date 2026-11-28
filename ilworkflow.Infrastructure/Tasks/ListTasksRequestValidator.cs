using Elsa.UserTasks.Models;
using FluentValidation;

namespace IlWorkflow.Infrastructure.Tasks;

/// <summary>Lives beside TaskService because the status filter is Elsa's UserTaskStatus enum.</summary>
public sealed class ListTasksRequestValidator : AbstractValidator<ListTasksRequest>
{
    private static readonly string[] Scopes = ["assigned", "available", "history", "all"];

    public ListTasksRequestValidator()
    {
        RuleFor(x => x.Scope).Must(scope => Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase))
            .WithMessage("scope must be assigned, available, history or all.");
        RuleFor(x => x.Limit).InclusiveBetween(1, 100).WithMessage("limit must be between 1 and 100.");
        RuleFor(x => x.Status).Must(status => Enum.TryParse<UserTaskStatus>(status, true, out var parsed) && Enum.IsDefined(parsed))
            .When(x => !string.IsNullOrWhiteSpace(x.Status)).WithMessage("Unknown task status.");
    }
}
