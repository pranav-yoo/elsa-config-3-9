using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elsa.Common.Models;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;
using FluentValidation;
using IlWorkflow.Infrastructure.Persistence;
using IlWorkflow.Infrastructure.Tasks;
using IlWorkflow.Infrastructure.Workflows;
using Microsoft.EntityFrameworkCore;

namespace IlWorkflow.API.Middleware;

/// <summary>
/// Runs before Elsa's built-in endpoints (/elsa/api/*) and adds this application's rules to them.
/// It adds no endpoints of its own. In short:
/// <list type="bullet">
/// <item>identity: local login/users, Development only.</item>
/// <item>workflow-definitions/{id}/execute: validate the ticket and set the requester from the token.</item>
/// <item>workflow-instances: ordinary users may read only instances they take part in.</item>
/// <item>user-tasks: native task API, plus the extra "observed" and "supervised" list scopes.</item>
/// <item>everything else (definitions admin, etc.): supervisors only.</item>
/// </list>
/// Errors are thrown as exceptions and turned into responses by <see cref="ExceptionHandlingMiddleware"/>.
/// </summary>
public sealed class WorkflowRequestGuard(RequestDelegate next, IConfiguration configuration, IHostEnvironment environment)
{
    private const string ElsaApiPath = "/elsa/api/";
    private const int MaxBodyBytes = 1024 * 1024;
    private readonly bool _development = environment.IsDevelopment();
    private readonly bool _preview = environment.IsDevelopment() && configuration.GetValue<bool>("SwaggerPreview");

    public async Task InvokeAsync(HttpContext http, ApplicationDbContext db, IWorkflowDefinitionStore definitions,
        WorkflowAccessService access, TaskService tasks)
    {
        var path = http.Request.Path.Value ?? "";
        var isAppApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
        var isElsaApi = path.StartsWith(ElsaApiPath, StringComparison.OrdinalIgnoreCase);

        if (_preview) RejectWritesInPreview(http, path, isAppApi, isElsaApi);
        if (isAppApi) RejectTenant(http);
        if (!isElsaApi)
        {
            await next(http);
            return;
        }

        // ASP.NET routing ignores case, so compare lower-case names; otherwise "/USER-TASKS" could skip a check.
        // Only the resource (parts[0]) and action (parts[2]) are lower-cased: IDs in parts[1] keep their case.
        var parts = path[ElsaApiPath.Length..].Trim('/').Split('/');
        parts[0] = parts[0].ToLowerInvariant();
        if (parts.Length > 2) parts[2] = parts[2].ToLowerInvariant();
        var resource = parts[0];
        var originalBody = http.Request.Body;
        try
        {
            switch (resource)
            {
                case "identity":
                    if (!_development) throw new ApiException(404, "Not found.");
                    break;
                case "workflow-definitions" when parts is [_, _, "execute"]:
                    var supervisor = RequireUser(http);
                    await PrepareExecuteAsync(http, parts[1], db, definitions, supervisor);
                    break;
                case "workflow-instances":
                    if (!RequireUser(http)) await EnsureCanReadInstanceAsync(http, parts, access);
                    break;
                case "user-tasks":
                    RequireUser(http);
                    if (await TryListExtraScopesAsync(http, parts, tasks)) return;
                    break;
                default:
                    var canAdminister = RequireUser(http);
                    var isDefinitionRead = resource == "workflow-definitions" && HttpMethods.IsGet(http.Request.Method);
                    if (!canAdminister && !isDefinitionRead)
                        throw new ApiException(403, "This engine operation is restricted. Use the task endpoints or POST execute.");
                    break;
            }

            // Task commands on parallel review tasks are run one at a time per workflow instance.
            if (resource == "user-tasks" && parts.Length >= 3 && !IsRead(http)
                && http.RequestServices.GetService<ParallelTaskCommands>() is { } parallel)
            {
                await parallel.RunCommandAsync(parts[1], parts[2] == "complete", http.User, () => next(http), http.RequestAborted);
                return;
            }
            await next(http);
        }
        finally
        {
            // PrepareExecuteAsync may replace the body with a rewritten copy; restore the original stream.
            if (http.Request.Body != originalBody) await http.Request.Body.DisposeAsync();
            http.Request.Body = originalBody;
        }
    }

    /// <summary>Checks the caller is signed in to the default organization. Returns true for supervisors.</summary>
    private static bool RequireUser(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated != true) throw new ApiException(401, "A valid bearer token is required.");
        RejectTenant(http);
        return PermissionChecks.IsSupervisor(http.User);
    }

    private static void RejectTenant(HttpContext http)
    {
        if (!string.IsNullOrEmpty(http.User.FindFirstValue("tenant")))
            throw new ApiException(403, "This version supports the default organization only.");
    }

    /// <summary>Swagger preview keeps Elsa in memory; block every write so nothing mixes with PostgreSQL.</summary>
    private static void RejectWritesInPreview(HttpContext http, string path, bool isAppApi, bool isElsaApi)
    {
        var isLogin = path.TrimEnd('/').Equals("/elsa/api/identity/login", StringComparison.OrdinalIgnoreCase);
        if (isAppApi && path.StartsWith("/api/workflow-configurations", StringComparison.OrdinalIgnoreCase) || isElsaApi && !IsRead(http) && !isLogin)
            throw new ApiException(503, "Swagger preview is documentation-only. Set SwaggerPreview=false and configure PostgreSQL.");
    }

    private static bool IsRead(HttpContext http) => HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);

    /// <summary>
    /// Validates POST execute and rewrites its body BEFORE Elsa creates an instance:
    /// the requester comes from the token and the exact published version is pinned.
    /// </summary>
    private static async Task PrepareExecuteAsync(HttpContext http, string definitionId, ApplicationDbContext db,
        IWorkflowDefinitionStore definitions, bool supervisor)
    {
        if (!HttpMethods.IsPost(http.Request.Method)) throw new ApiException(400, "Use POST execute with a JSON body.");
        if (!PermissionChecks.HasPermission(http.User, "workflows/definitions", "execute")) throw new ApiException(403, "Workflow execute permission is required.");

        var definition = await definitions.FindAsync(new WorkflowDefinitionFilter { DefinitionId = definitionId, VersionOptions = VersionOptions.Published }, http.RequestAborted)
            ?? throw new ApiException(404, "Published definition not found.");
        var row = await db.WorkflowConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.DefinitionVersionId == definition.Id, http.RequestAborted);
        if (row is null)
        {
            // A definition not created through /api/workflow-configurations: supervisors may run it unchanged.
            if (!supervisor) throw new ApiException(403, "Only configured published workflows can be submitted.");
            return;
        }
        if (!row.Ready || !row.Frozen || WorkflowConfigurationService.Hash(definition.StringData) != row.DefinitionHash)
            throw new ApiException(409, "This workflow version is not fully published or was changed outside the configuration API.");

        var body = await ReadJsonObjectAsync(http);
        await new ExecuteRequestValidator().ValidateAndThrowAsync(body, http.RequestAborted);

        var requester = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(requester)) throw new ApiException(403, "A stable authenticated user ID is required.");
        var input = body["input"] as JsonObject ?? [];
        if (input[TicketState.Requester] is { } supplied && supplied.ToString() != requester)
            throw new ApiException(403, "You cannot submit a ticket on behalf of someone else.");

        body["input"] = new JsonObject
        {
            [TicketState.Requester] = requester,
            [TicketState.Attributes] = input[TicketState.Attributes]?.DeepClone() ?? new JsonObject()
        };
        // Always run the version this row belongs to: never a draft, never a newer version published meanwhile.
        body["versionOptions"] = JsonSerializer.SerializeToNode(VersionOptions.SpecificVersion(row.DefinitionVersion));

        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        http.Request.Body = new MemoryStream(bytes);
        http.Request.ContentLength = bytes.Length;
        http.Request.ContentType = "application/json";
    }

    /// <summary>Ordinary users may read one instance (and its journal) only when they take part in it.</summary>
    private static async Task EnsureCanReadInstanceAsync(HttpContext http, string[] parts, WorkflowAccessService access)
    {
        var method = http.Request.Method;
        var isSingleRead = parts.Length == 2 && HttpMethods.IsGet(method);
        var isJournalRead = parts is [_, _, "journal"] && (HttpMethods.IsGet(method) || HttpMethods.IsPost(method));
        if (!isSingleRead && !isJournalRead) throw new ApiException(403, "Instance queries and administration are restricted to supervisors.");
        if (!await access.CanReadInstanceAsync(parts[1], http.User, http.RequestAborted)) throw new ApiException(404, "Workflow instance not found.");
    }

    /// <summary>
    /// GET /elsa/api/user-tasks?scope=observed|supervised. Elsa's own scopes cannot express these,
    /// so they are answered here by <see cref="TaskService"/> (the same code behind /api/tasks).
    /// </summary>
    private static async Task<bool> TryListExtraScopesAsync(HttpContext http, string[] parts, TaskService tasks)
    {
        if (parts.Length != 1 || !HttpMethods.IsGet(http.Request.Method)) return false;
        var query = http.Request.Query;

        // Elsa 3.9's keyword search cannot be translated to SQL by its PostgreSQL provider.
        if (!string.IsNullOrWhiteSpace(query["search"]))
            throw new ApiException(400, "Keyword task search is unavailable with PostgreSQL. Use scope, status or workflow filters.");

        var scope = query["scope"].ToString();
        if (!scope.Equals("observed", StringComparison.OrdinalIgnoreCase) && !scope.Equals("supervised", StringComparison.OrdinalIgnoreCase))
            return false;

        var limit = 50;
        if (query.TryGetValue("limit", out var rawLimit) && !int.TryParse(rawLimit, out limit)) throw new ApiException(400, "limit must be a number.");
        var includeTotalCount = false;
        if (query.TryGetValue("includeTotalCount", out var rawTotal) && !bool.TryParse(rawTotal, out includeTotalCount))
            throw new ApiException(400, "includeTotalCount must be true or false.");

        var request = new ListTasksRequest(scope, query["status"], query["workflowInstanceId"], query["cursor"], limit, includeTotalCount);
        await http.Response.WriteAsJsonAsync(await tasks.ListAsync(http.User, request, http.RequestAborted), http.RequestAborted);
        return true;
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(HttpContext http)
    {
        if (http.Request.ContentLength > MaxBodyBytes) throw new ApiException(413, "Request is too large.");
        // Also limit chunked requests, which have no Content-Length.
        var limit = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxBodyBytes;

        // Reject duplicate keys now; otherwise JsonObject could throw later and cause a 500.
        var options = new JsonDocumentOptions { AllowDuplicateProperties = false };
        return await JsonNode.ParseAsync(http.Request.Body, documentOptions: options, cancellationToken: http.RequestAborted) as JsonObject
            ?? throw new ApiException(400, "Supply a JSON object.");
    }
}
