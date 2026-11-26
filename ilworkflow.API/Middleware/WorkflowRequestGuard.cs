using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elsa.Common.Models;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;
using FluentValidation;
using IlWorkflow.Api.Workflows;
using IlWorkflow.Infrastructure.Persistence;
using IlWorkflow.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;

namespace IlWorkflow.API.Middleware;

/// <summary>
/// Attaches business validation and visibility to Elsa's native routes (it adds no routes of its own) and maps
/// exceptions thrown anywhere under /api to the Result body.
/// </summary>
public sealed class WorkflowRequestGuard(RequestDelegate next, IConfiguration configuration, IHostEnvironment environment)
{
    private const string ConfigurationsPath = "/api/workflow-configurations";
    private const string ElsaApiPath = "/elsa/api/";
    private const int MaxBodyBytes = 1024 * 1024;
    private readonly bool _development = environment.IsDevelopment();
    private readonly bool _preview = environment.IsDevelopment() && configuration.GetValue<bool>("SwaggerPreview");

    public async Task InvokeAsync(HttpContext http, ApplicationDbContext db, IWorkflowDefinitionStore definitions,
        WorkflowAccessService access, TaskService tasks)
    {
        var originalBody = http.Request.Body;
        try
        {
            await GuardAsync(http, db, definitions, access, tasks);
        }
        catch (ValidationException exception)
        {
            var errors = exception.Errors.Select(x => new { field = x.PropertyName, message = x.ErrorMessage }).ToArray();
            await ApiResultHelper.WriteErrorAsync(http, 400, "validation_failed", string.Join(" ", errors.Select(x => x.message)), errors);
        }
        catch (ApiException exception)
        {
            await ApiResultHelper.WriteErrorAsync(http, exception.Status, "request_rejected", exception.Message);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException
            { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Two simultaneous task commands can both pass the initial operation lookup. The native
            // store's unique index remains authoritative; expose its race as a conflict, not HTTP 500.
            await ApiResultHelper.WriteErrorAsync(http, 409, "operation_conflict", "A competing operation was already recorded. Reload the task before deciding whether to retry.");
        }
        catch (DbUpdateConcurrencyException)
        {
            await ApiResultHelper.WriteErrorAsync(http, 409, "revision_conflict", "Read the current revision and retry the intended change.");
        }
        catch (BadHttpRequestException exception)
        {
            await ApiResultHelper.WriteErrorAsync(http, exception.StatusCode, "invalid_request", "Check request size and content type.");
        }
        catch (JsonException)
        {
            await ApiResultHelper.WriteErrorAsync(http, 400, "invalid_json", "Check JSON types, enum names and required objects.");
        }
        finally
        {
            // PrepareExecuteAsync replaces the body with the normalized copy; hand the original stream back.
            if (http.Request.Body != originalBody) await http.Request.Body.DisposeAsync();
            http.Request.Body = originalBody;
        }
    }

    private async Task GuardAsync(HttpContext http, ApplicationDbContext db, IWorkflowDefinitionStore definitions,
        WorkflowAccessService access, TaskService tasks)
    {
        var path = http.Request.Path.Value ?? "";
        var isConfigurations = path.StartsWith(ConfigurationsPath, StringComparison.OrdinalIgnoreCase);
        var isElsa = path.StartsWith(ElsaApiPath, StringComparison.OrdinalIgnoreCase);
        // A preview must never mix application PostgreSQL writes with Elsa's temporary memory stores.
        // Local login remains available for browsing authorized documentation; business operations do not.
        if (_preview && (isConfigurations || isElsa && !IsRead(http) && !path.TrimEnd('/').Equals("/elsa/api/identity/login", StringComparison.OrdinalIgnoreCase)))
            throw new ApiException(503, "Swagger preview is documentation-only. Set SwaggerPreview=false and configure PostgreSQL for workflow operations.");
        if (isConfigurations)
        {
            RejectTenant(http);
            await next(http);
            return;
        }
        if (!isElsa) { await next(http); return; }

        var parts = path[ElsaApiPath.Length..].Trim('/').Split('/');
        // ASP.NET routes are case-insensitive. Normalize resource/action names too, or differently
        // cased URLs could reach the native endpoint without running the corresponding guard.
        parts[0] = parts[0].ToLowerInvariant();
        if (parts.Length > 2) parts[2] = parts[2].ToLowerInvariant();
        if (parts[0] == "identity")
        {
            // Local Elsa login/users/roles exist for Development only; production uses uid tokens.
            if (!_development) throw new ApiException(404, "Not found.");
            await next(http);
            return;
        }
        if (http.User.Identity?.IsAuthenticated != true) throw new ApiException(401, "A valid bearer token is required.");
        RejectTenant(http);
        var supervisor = PermissionChecks.IsSupervisor(http.User);

        if (parts[0] == "user-tasks" && parts.Length == 1 && HttpMethods.IsGet(http.Request.Method))
        {
            var scope = http.Request.Query["scope"].ToString();
            // Two extra read scopes: observed (viewableByStages) and supervised (workflow supervisors).
            if (scope.Equals("observed", StringComparison.OrdinalIgnoreCase) || scope.Equals("supervised", StringComparison.OrdinalIgnoreCase))
            {
                await ListObservedTasksAsync(http, tasks, scope);
                return;
            }
            // Elsa 3.9 keyword search includes Tags.Any(). Its PostgreSQL migration stores tags as
            // text JSON; the compatibility converter cannot translate that collection predicate.
            // Scope/status/instance/reference filters remain native and fully supported.
            if (!string.IsNullOrWhiteSpace(http.Request.Query["search"]))
                throw new ApiException(400, "Keyword task search is unavailable with the Elsa 3.9 PostgreSQL provider. Use scope, status or workflow filters.");
        }
        if (parts[0] == "workflow-definitions" && parts.Length == 3 && parts[2] == "execute")
            await PrepareExecuteAsync(http, parts[1], db, definitions, supervisor);
        else if (parts[0] == "workflow-instances")
        {
            if (!supervisor) await EnsureCanReadInstanceAsync(http, parts, access);
        }
        else if (parts[0] != "user-tasks" && !supervisor && (parts[0] != "workflow-definitions" || !HttpMethods.IsGet(http.Request.Method)))
            throw new ApiException(403, "This engine operation is restricted. Use native task operations or POST execute.");

        // Native task mutations on parallel review tasks are serialized per workflow instance.
        if (parts[0] == "user-tasks" && parts.Length >= 3 && !IsRead(http)
            && http.RequestServices.GetService<ParallelTaskCommands>() is { } parallel)
        {
            await parallel.RunCommandAsync(parts[1], parts[2] == "complete", http.User, () => next(http), http.RequestAborted);
            return;
        }
        await next(http);
    }

    private static bool IsRead(HttpContext http) => HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);

    private static void RejectTenant(HttpContext http)
    {
        if (!string.IsNullOrEmpty(http.User.FindFirstValue("tenant"))) throw new ApiException(403, "This version supports the default organization only.");
    }

    /// <summary>Validate and rewrite POST execute BEFORE Elsa creates an instance, so invalid submissions are HTTP 400 and not persisted Faulted instances.</summary>
    private static async Task PrepareExecuteAsync(HttpContext http, string definitionId, ApplicationDbContext db,
        IWorkflowDefinitionStore definitions, bool supervisor)
    {
        if (!HttpMethods.IsPost(http.Request.Method)) throw new ApiException(400, "Use POST execute with a JSON input object.");
        if (!PermissionChecks.HasPermission(http.User, "workflows/definitions", "execute")) throw new ApiException(403, "Workflow execute permission is required.");
        var definition = await definitions.FindAsync(new WorkflowDefinitionFilter { DefinitionId = definitionId, VersionOptions = VersionOptions.Published }, http.RequestAborted);
        if (definition is null) throw new ApiException(404, "Published definition not found.");
        var row = await db.WorkflowConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.DefinitionVersionId == definition.Id, http.RequestAborted);
        if (row is null)
        {
            // Definitions that were not created through the configuration API are administrator-only through this entry boundary.
            if (!supervisor) throw new ApiException(403, "Only configured published workflows can be submitted by ordinary users.");
            return;
        }
        if (!row.Ready || !row.Frozen || WorkflowConfigurationService.Hash(definition.StringData ?? "") != row.DefinitionHash)
            throw new ApiException(409, "Workflow publication/configuration is incomplete or changed outside its configuration API.");

        var body = await ReadObjectAsync(http);
        if (body.Any(property => property.Key is not ("input" or "correlationId" or "name" or "versionOptions")))
            throw new ApiException(400, "Custom variables and alternate activity starts are not permitted for configured tickets.");
        var input = body["input"] as JsonObject ?? new JsonObject();
        var requester = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(requester)) throw new ApiException(403, "A stable authenticated user ID is required.");
        if (input["requesterId"] is { } supplied && supplied.ToString() != requester) throw new ApiException(403, "Requester impersonation is not permitted.");
        if (input.Any(x => x.Key != "attributes" && x.Key != "requesterId")) throw new ApiException(400, "Supply ticket fields inside input.attributes.");
        var setup = ConfigurationJson.Read<WorkflowSetup>(row.ConfigurationJson);
        var values = input["attributes"] is JsonObject attributeObject
            ? attributeObject.Deserialize<Dictionary<string, JsonElement>>()! : new Dictionary<string, JsonElement>();
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

    /// <summary>Ordinary users may read only their own instance (and its journal); everything else is for supervisors.</summary>
    private static async Task EnsureCanReadInstanceAsync(HttpContext http, string[] parts, WorkflowAccessService access)
    {
        var read = parts.Length == 2 && HttpMethods.IsGet(http.Request.Method)
            || parts.Length == 3 && parts[2] == "journal" && (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsPost(http.Request.Method));
        if (!read) throw new ApiException(403, "Broad instance queries and administration are restricted to supervisors.");
        if (!await access.CanReadInstanceAsync(parts[1], http.User, http.RequestAborted)) throw new ApiException(404, "Workflow instance not found.");
    }

    /// <summary>Elsa's query scope enum is fixed, so the observed/supervised scopes are served here by the same TaskService as /api/tasks.</summary>
    private static async Task ListObservedTasksAsync(HttpContext http, TaskService tasks, string scope)
    {
        var query = http.Request.Query;
        var limit = 50;
        if (query.TryGetValue("limit", out var rawLimit) && !int.TryParse(rawLimit, out limit)) throw new ApiException(400, "limit must be a number.");
        var includeTotalCount = false;
        if (query.TryGetValue("includeTotalCount", out var rawTotal) && !bool.TryParse(rawTotal, out includeTotalCount))
            throw new ApiException(400, "includeTotalCount must be true or false.");
        var result = await tasks.ListAsync(http.User, scope, query["status"], query["workflowInstanceId"], query["cursor"], limit, includeTotalCount, http.RequestAborted);
        await http.Response.WriteAsJsonAsync(result, http.RequestAborted);
    }

    private static async Task<JsonObject> ReadObjectAsync(HttpContext http)
    {
        if (http.Request.ContentLength > MaxBodyBytes) throw new ApiException(413, "Request is too large.");
        // Also cap chunked requests, where ContentLength is absent. Kestrel enforces this while reading.
        var limit = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxBodyBytes;
        // Reject duplicate keys at parsing time. Otherwise JsonObject's lazy dictionary can throw
        // ArgumentException later, turning malformed client input into an HTTP 500.
        return await JsonNode.ParseAsync(http.Request.Body, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false },
            cancellationToken: http.RequestAborted) as JsonObject
            ?? throw new ApiException(400, "Supply a JSON object.");
    }
}
