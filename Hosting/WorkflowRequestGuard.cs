using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elsa.Common.Models;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;
using FluentValidation;
using IlWorkflow.Api.Configuration;
using IlWorkflow.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IlWorkflow.Api.Hosting;

/// <summary>Attaches business validation and visibility to existing Elsa routes, not replacement ticket APIs.</summary>
public sealed class WorkflowRequestGuard
{
    private readonly RequestDelegate _next;
    private readonly bool _preview;

    // Middleware receives the next pipeline step; it does not introduce new HTTP routes.
    public WorkflowRequestGuard(RequestDelegate next, IConfiguration configuration, IHostEnvironment environment)
    {
        _next = next;
        _preview = environment.IsDevelopment() && configuration.GetValue<bool>("SwaggerPreview");
    }
    public async Task InvokeAsync(HttpContext http, ApplicationDbContext db, IWorkflowDefinitionStore definitions, WorkflowAccessService access)
    {
        var originalBody = http.Request.Body;
        try
        {
            var path = http.Request.Path.Value ?? "";
            // A preview must never mix application PostgreSQL writes with Elsa's temporary memory stores.
            // Local login remains available for browsing authorized documentation; business operations do not.
            if (_preview && (path.StartsWith("/api/workflow-configurations", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/elsa/api/", StringComparison.OrdinalIgnoreCase)
                    && !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
                    && !path.TrimEnd('/').Equals("/elsa/api/identity/login", StringComparison.OrdinalIgnoreCase)))
                throw new ApiException(503, "Swagger preview is documentation-only. Set SwaggerPreview=false and configure PostgreSQL for workflow operations.");
            if (path.StartsWith("/api/workflow-configurations", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(http.User.FindFirstValue("tenant"))) throw new ApiException(403, "This version supports the default organization only.");
                await _next(http); return;
            }
            if (!path.StartsWith("/elsa/api/", StringComparison.OrdinalIgnoreCase)) { await _next(http); return; }
            var parts = path[10..].Trim('/').Split('/');
            // ASP.NET routes are case-insensitive. Normalize resource/action names too, or differently
            // cased URLs could reach the native endpoint without running the corresponding guard.
            parts[0] = parts[0].ToLowerInvariant();
            if (parts.Length > 2) parts[2] = parts[2].ToLowerInvariant();
            if (parts[0].Equals("identity", StringComparison.OrdinalIgnoreCase)) { await _next(http); return; }
            if (http.User.Identity?.IsAuthenticated != true) throw new ApiException(401, "A valid bearer token is required.");
            if (!string.IsNullOrEmpty(http.User.FindFirstValue("tenant"))) throw new ApiException(403, "This version supports the default organization only.");
            var supervisor = ConfigurationAccess.IsSupervisor(http.User);
            // Elsa 3.9 keyword search includes Tags.Any(). Its PostgreSQL migration stores tags as
            // text JSON; the compatibility converter cannot translate that collection predicate.
            // Scope/status/instance/reference filters remain native and fully supported.
            if (parts[0] == "user-tasks" && parts.Length == 1 && !string.IsNullOrWhiteSpace(http.Request.Query["search"]))
                throw new ApiException(400, "Keyword task search is unavailable with the Elsa 3.9 PostgreSQL provider. Use scope, status or workflow filters.");
            if (parts[0] == "workflow-definitions" && parts.Length == 3 && parts[2] == "execute")
            {
                if (!HttpMethods.IsPost(http.Request.Method)) throw new ApiException(400, "Use POST execute with a JSON input object.");
                if (!ConfigurationAccess.HasPermission(http.User, "workflows/definitions", "execute")) throw new ApiException(403, "Workflow execute permission is required.");
                var definition = await definitions.FindAsync(new WorkflowDefinitionFilter { DefinitionId = parts[1], VersionOptions = VersionOptions.Published }, http.RequestAborted);
                if (definition is null) throw new ApiException(404, "Published definition not found.");
                var row = await db.WorkflowConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.DefinitionVersionId == definition.Id, http.RequestAborted);
                if (row is null)
                {
                    // Legacy research workflows are deliberately administrator-only through this new entry boundary.
                    if (!supervisor) throw new ApiException(403, "Only configured published workflows can be submitted by ordinary users.");
                    await _next(http); return;
                }
                if (!row.Ready || !row.Frozen || WorkflowConfigurationService.Hash(definition.StringData ?? "") != row.DefinitionHash)
                    throw new ApiException(409, "Workflow publication/configuration is incomplete or changed outside its configuration API.");
                // Validate and rewrite the request BEFORE Elsa creates an instance. Invalid submissions
                // must be HTTP 400, not persisted Faulted instances. This path is synchronous execute only.
                var body = await ReadObjectAsync(http);
                if (body.Any(property => property.Key is not ("input" or "correlationId" or "name" or "versionOptions")))
                    throw new ApiException(400, "Custom variables and alternate activity starts are not permitted for configured tickets.");
                var input = body["input"] as JsonObject ?? new JsonObject();
                var requester = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");
                if (string.IsNullOrWhiteSpace(requester)) throw new ApiException(403, "A stable authenticated user ID is required.");
                if (input["requesterId"] is { } supplied && supplied.ToString() != requester) throw new ApiException(403, "Requester impersonation is not permitted.");
                var setup = ConfigurationJson.Read<WorkflowSetup>(row.ConfigurationJson);
                var values = input["attributes"] is JsonObject attributeObject
                    ? attributeObject.Deserialize<Dictionary<string, JsonElement>>()! : new Dictionary<string, JsonElement>();
                if (input.Any(x => x.Key != "attributes" && x.Key != "requesterId")) throw new ApiException(400, "Supply ticket fields inside input.attributes.");
                await new TicketInputValidator(setup).ValidateAndThrowAsync(values, http.RequestAborted);
                var normalizedInput = new JsonObject { ["requesterId"] = requester, ["attributes"] = JsonSerializer.SerializeToNode(values) };
                foreach (var field in definition.Inputs.Where(field => field.Name is not ("attributes" or "requesterId")))
                    if (values.TryGetValue(field.Name, out var value)) normalizedInput[field.Name] = JsonNode.Parse(value.GetRawText());
                body["input"] = normalizedInput;
                // Replacing client version selection prevents executing a draft or racing a later publication.
                body["versionOptions"] = JsonSerializer.SerializeToNode(VersionOptions.SpecificVersion(row.DefinitionVersion));
                var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
                http.Request.Body = new MemoryStream(bytes);
                http.Request.ContentLength = bytes.Length;
                http.Request.ContentType = "application/json";
            }
            else if (parts[0] == "workflow-instances")
            {
                if (!supervisor)
                {
                    var read = parts.Length == 2 && HttpMethods.IsGet(http.Request.Method)
                        || parts.Length == 3 && parts[2] == "journal" && (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsPost(http.Request.Method));
                    if (!read) throw new ApiException(403, "Broad instance queries and administration are restricted to supervisors.");
                    if (!await access.CanReadInstanceAsync(parts[1], http.User, http.RequestAborted)) throw new ApiException(404, "Workflow instance not found.");
                }
            }
            else if (parts[0] != "user-tasks")
            {
                if (!supervisor && (parts[0] != "workflow-definitions" || !HttpMethods.IsGet(http.Request.Method)))
                    throw new ApiException(403, "This engine operation is restricted. Use native task operations or POST execute.");
            }
            await _next(http);
        }
        catch (ValidationException exception)
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { code = "validation_failed", errors = exception.Errors.Select(x => new { field = x.PropertyName, message = x.ErrorMessage }) });
        }
        catch (ApiException exception)
        {
            http.Response.StatusCode = exception.Status;
            await http.Response.WriteAsJsonAsync(new { code = "request_rejected", message = exception.Message });
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException
            { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Two simultaneous task commands can both pass the initial operation lookup. The native
            // store's unique index remains authoritative; expose its race as a conflict, not HTTP 500.
            http.Response.StatusCode = 409;
            await http.Response.WriteAsJsonAsync(new { code = "operation_conflict", message = "A competing operation was already recorded. Reload the task before deciding whether to retry." });
        }
        catch (DbUpdateConcurrencyException)
        {
            http.Response.StatusCode = 409;
            await http.Response.WriteAsJsonAsync(new { code = "revision_conflict", message = "Read the current revision and retry the intended change." });
        }
        catch (BadHttpRequestException exception)
        {
            http.Response.StatusCode = exception.StatusCode;
            await http.Response.WriteAsJsonAsync(new { code = "invalid_request", message = "Check request size and content type." });
        }
        catch (JsonException)
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { code = "invalid_json", message = "Check JSON types, enum names and required objects." });
        }
        finally
        {
            if (http.Request.Body != originalBody) await http.Request.Body.DisposeAsync();
            http.Request.Body = originalBody;
        }
    }
    private static async Task<JsonObject> ReadObjectAsync(HttpContext http)
    {
        if (http.Request.ContentLength > 1024 * 1024) throw new ApiException(413, "Request is too large.");
        // Also cap chunked requests, where ContentLength is absent. Kestrel enforces this while reading.
        var limit = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 1024 * 1024;
        // The original stream is restored after native execution; downstream reads the normalized replacement.
        // Reject duplicate keys at parsing time. Otherwise JsonObject's lazy dictionary can throw
        // ArgumentException later, turning malformed client input into an HTTP 500.
        return await JsonNode.ParseAsync(http.Request.Body, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false },
            cancellationToken: http.RequestAborted) as JsonObject
            ?? throw new ApiException(400, "Supply a JSON object.");
    }
}
