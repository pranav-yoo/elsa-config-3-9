using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;

namespace IlWorkflow.API.Common;

/// <summary>One place that builds <see cref="Result{T}"/> responses. Controllers return these instead of Ok()/Accepted()/Conflict().</summary>
public static class ApiResultHelper
{
    public static ObjectResult Ok<T>(T data, params string[] messages) => Create(StatusCodes.Status200OK, data, messages);
    public static ObjectResult Accepted<T>(T data, params string[] messages) => Create(StatusCodes.Status202Accepted, data, messages);
    public static ObjectResult Conflict<T>(T data, params string[] messages) => Create(StatusCodes.Status409Conflict, data, messages);

    /// <summary>Error body: data carries a machine-readable code (and field errors), messages carry the text.</summary>
    public static ObjectResult Error(int statusCode, string code, string message, object? errors = null) =>
        Create(statusCode, errors is null ? new { code } : (object)new { code, errors }, message);

    /// <summary>Message used for any 2xx result that has no message of its own.</summary>
    public const string DefaultSuccessMessage = "Executed Successfully";

    public static ObjectResult Create<T>(int statusCode, T? data, params string[] messages)
    {
        var list = messages.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        if (list.Count == 0 && statusCode is >= 200 and <= 299) list.Add(DefaultSuccessMessage);
        return new(new Result<T>(statusCode, list, data)) { StatusCode = statusCode };
    }

    /// <summary>Write a result outside MVC (middleware) with the same serializer settings as controllers.</summary>
    public static Task WriteAsync(HttpContext http, ObjectResult result) =>
        result.ExecuteResultAsync(new ActionContext(http, http.GetRouteData(), new ActionDescriptor()));
}
