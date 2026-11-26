using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IlWorkflow.API.Filters;

/// <summary>Model-binding failures (malformed body, wrong types) never reach a controller action; give them the same Result shape.</summary>
public sealed class ApiResponseFilter : IAsyncResultFilter
{
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: ValidationProblemDetails problem } && ApiResultHelper.Applies(context.HttpContext))
        {
            var errors = problem.Errors.SelectMany(e => e.Value.Select(m => new { field = e.Key, message = m })).ToArray();
            context.Result = ApiResultHelper.Error(StatusCodes.Status400BadRequest, "invalid_request", string.Join(" ", errors.Select(e => e.message)), errors);
        }
        return next();
    }
}
