using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;

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

    /// <summary>Elsa's native /elsa/api/* endpoints keep Elsa's own format; everything under /api uses Result&lt;T&gt;.</summary>
    public static bool Applies(HttpContext http) => http.Request.Path.StartsWithSegments("/api");

    /// <summary>Error body: Result&lt;T&gt; for /api/*, otherwise the previous { code, message, errors } shape.</summary>
    public static Task WriteErrorAsync(HttpContext http, int status, string code, string message, object? errors = null)
    {
        http.Response.StatusCode = status;
        if (Applies(http)) return WriteAsync(http, Error(status, code, message, errors));
        return errors is null ? http.Response.WriteAsJsonAsync(new { code, message }) : http.Response.WriteAsJsonAsync(new { code, errors });
    }

    /// <summary>Write a result outside MVC (middleware) with the same serializer settings as controllers.</summary>
    public static Task WriteAsync(HttpContext http, ObjectResult result) =>
        result.ExecuteResultAsync(new ActionContext(http, http.GetRouteData(), new ActionDescriptor()));
}
