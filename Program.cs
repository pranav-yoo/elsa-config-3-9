using System.Diagnostics;
using IlWorkflow.Api.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Elsa.Extensions;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;

using Npgsql;

using Elsa.Workflows.Activities.Flowchart.Extensions;
using Elsa.Persistence.EFCore.Modules.Identity;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Activities;
using Elsa.UserTasks.Services;
using Elsa.UserTasks.Persistence.EFCore.Extensions;
using Elsa.UserTasks.Persistence.EFCore.PostgreSql.Extensions;
using IlWorkflow.Api.Tickets;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("Samples/external-users.json", optional: false, reloadOnChange: false);
// Reapply normal overrides after the research directory defaults.
builder.Configuration.AddJsonFile("appsettings.json", optional: false);
builder.Configuration.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true);
if (builder.Environment.IsDevelopment()) builder.Configuration.AddUserSecrets<Program>();
builder.Configuration.AddEnvironmentVariables().AddCommandLine(args);
var useConfigurationIdentity = builder.Environment.IsDevelopment() && builder.Configuration.GetValue("Identity:UseConfigurationProviders", true);
if (useConfigurationIdentity) builder.Configuration.AddJsonFile("Samples/identity.json", optional: false, reloadOnChange: true);
var configuration = builder.Configuration;
var swaggerPreview = builder.Environment.IsDevelopment() && configuration.GetValue<bool>("SwaggerPreview");
var identityPostgres = configuration.GetValue("Identity:UsePostgreSql", !swaggerPreview);
if (identityPostgres && useConfigurationIdentity)
    throw new InvalidOperationException("Set Identity:UseConfigurationProviders=false when Identity:UsePostgreSql=true so users authenticate against the database.");
var requiresPostgres = identityPostgres || !swaggerPreview;
var connectionString = configuration.GetConnectionString("Elsa")!;
var signingKey = configuration["Identity:Tokens:SigningKey"];
if (!requiresPostgres && string.IsNullOrWhiteSpace(signingKey))
    configuration["Identity:Tokens:SigningKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
if (requiresPostgres && (string.IsNullOrWhiteSpace(signingKey) || signingKey.Length < 32))
    throw new InvalidOperationException("Set Identity:Tokens:SigningKey to a random secret of at least 32 characters using dotnet user-secrets.");
if (!useConfigurationIdentity && string.IsNullOrWhiteSpace(configuration["Identity:Admin:Password"]))
    throw new InvalidOperationException("Set Identity:Admin:Password using dotnet user-secrets.");

builder.Services.AddElsa(elsa =>
{
    elsa.UseIdentity(identity =>
        {
            identity.TokenOptions += options => configuration.GetSection("Identity:Tokens").Bind(options);
            if (useConfigurationIdentity)
            {
                identity.UseConfigurationBasedUserProvider(options => configuration.GetSection("Identity").Bind(options));
                identity.UseConfigurationBasedRoleProvider(options => configuration.GetSection("Identity").Bind(options));
            }
            if (identityPostgres) identity.UseEntityFrameworkCore(ef => ef.UsePostgreSql(connectionString));
            if (!useConfigurationIdentity) identity.UseDefaultAdmin(admin => admin
                .WithAdminUserName(configuration["Identity:Admin:UserName"] ?? "admin")
                .WithAdminRoleName("admin")
                .WithAdminPassword(configuration["Identity:Admin:Password"]!));
        })
        .UseDefaultAuthentication()
        .UseWorkflowManagement(management => { if (!swaggerPreview) management.UseEntityFrameworkCore(ef => ef.UsePostgreSql(connectionString)); })
        .UseWorkflowRuntime(runtime => { if (!swaggerPreview) runtime.UseEntityFrameworkCore(ef => ef.UsePostgreSql(connectionString)); })
        .UseWorkflowsApi()
        .UseUserTasks(tasks =>
        {
            if (!swaggerPreview) tasks.UseEntityFrameworkCore(ef => ef.UsePostgreSql(connectionString));
        })
        .UseFlowchart(flowchart => flowchart.UseTokenBasedExecution())
        .UseJavaScript()
        .AddActivitiesFrom<UserTask>()
        .AddActivitiesFrom<TicketFlow>()
        .UseHttp(http => http.ConfigureHttpOptions = options => configuration.GetSection("Http").Bind(options))
        ;
    if (builder.Environment.IsDevelopment())
        elsa.AddSwagger();
});

// Elsa 3.9 defaults capture the root provider; both services resolve scoped runtime/mediator services.
builder.Services.AddScoped<IUserTaskNotificationSink, DefaultUserTaskNotificationSink>();
builder.Services.AddScoped<IUserTaskWorkflowResumer, DefaultUserTaskWorkflowResumer>();
builder.Services.AddSingleton<IUserTaskFormProvider, TicketFormProvider>();
// Development accounts come from Elsa's configuration providers, rather than store seeding.
if (useConfigurationIdentity)
    builder.Services.Configure<Elsa.Identity.Options.DefaultAdminUserOptions>(options =>
    {
        options.AdminUserName = configuration["Identity:Admin:UserName"] ?? "admin";
        options.AdminPassword = configuration["Identity:Admin:Password"] ?? "";
    });




builder.Services.AddHttpContextAccessor();
builder.Services.AddCompanyAuthentication(configuration, builder.Environment);
builder.Services.AddScoped<TicketIdentity>();

builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "HH:mm:ss ";
    options.IncludeScopes = true;
});
builder.Services.PostConfigureAll<JwtBearerOptions>(options =>
{
    var previous = options.Events.OnAuthenticationFailed;
    options.Events.OnAuthenticationFailed = async context =>
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Authentication");
        logger.LogWarning("Bearer authentication failed: {FailureType}. Sign in again and use the accessToken in Swagger Authorize. TraceId={TraceId}",
            context.Exception.GetType().Name, context.HttpContext.TraceIdentifier);
        if (previous is not null) await previous(context);
    };
});

var app = builder.Build();
app.UseExceptionHandler(errors => errors.Run(async context =>
{
    context.Response.StatusCode = 500;
    context.Response.Headers["X-Trace-Id"] = context.TraceIdentifier;
    await context.Response.WriteAsJsonAsync(new { Code = "internal_error", Message = "An unexpected error occurred. Check server logs using the trace ID.", TraceId = context.TraceIdentifier });
}));
app.Logger.LogInformation("Workflow/task storage: {WorkflowStorage}; identity storage: {IdentityStorage}",
    swaggerPreview ? "in memory" : "PostgreSQL",
    identityPostgres ? "PostgreSQL" : useConfigurationIdentity ? "sample JSON providers" : "in memory");
var databaseSettings = new NpgsqlConnectionStringBuilder(connectionString);
app.Logger.LogInformation("Database configured: Host={Host}; Port={Port}; Database={Database}; Username={Username}; SSL={SslMode}. Credentials omitted.",
    databaseSettings.Host, databaseSettings.Port, databaseSettings.Database, databaseSettings.Username, databaseSettings.SslMode);
if (!requiresPostgres)
{
    app.Logger.LogInformation("All stores use preview/sample providers; PostgreSQL connections and migrations are skipped.");
}
else if (app.Environment.IsDevelopment())
{
    if (databaseSettings.Database != "ilworkflow")
        throw new InvalidOperationException("Development database creation is restricted to ilworkflow.");
    var started = Stopwatch.GetTimestamp();
    try
    {
        var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        app.Logger.LogInformation("Opening PostgreSQL maintenance connection to check database {Database}", databaseSettings.Database);
        await using var connection = new NpgsqlConnection(maintenance.ConnectionString);
        await connection.OpenAsync();
        app.Logger.LogInformation("PostgreSQL connection established in {ElapsedMs:F1} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", databaseSettings.Database);
        if (await exists.ExecuteScalarAsync() is null)
        {
            app.Logger.LogInformation("Creating database ilworkflow");
            await using var create = new NpgsqlCommand("CREATE DATABASE ilworkflow", connection);
            try { await create.ExecuteNonQueryAsync(); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateDatabase)
            { app.Logger.LogInformation("Database ilworkflow was created by another process"); }
        }
        else app.Logger.LogInformation("Database ilworkflow already exists");
        app.Logger.LogInformation("Database initialization complete. Elsa will apply migrations for the enabled PostgreSQL stores during startup.");
    }
    catch (Exception exception)
    {
        app.Logger.LogError("PostgreSQL initialization failed: {FailureType}; SQLSTATE={SqlState}; Host={Host}; Port={Port}; Database={Database}. Check PostgreSQL readiness, credentials and database permissions.",
            exception.GetType().Name, (exception as PostgresException)?.SqlState, databaseSettings.Host, databaseSettings.Port, databaseSettings.Database);
        throw;
    }
}
app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation(
    "Application ready. IdentityPostgreSql={IdentityPostgreSql}; WorkflowPostgreSql={WorkflowPostgreSql}; enabled database migrations completed.",
    identityPostgres, !swaggerPreview));

app.Use(async (context, next) =>
{
    var started = Stopwatch.GetTimestamp();
    using var scope = app.Logger.BeginScope("TraceId={TraceId}", context.TraceIdentifier);
    context.Response.Headers["X-Trace-Id"] = context.TraceIdentifier;
    try
    {
        await next(context);
    }
    catch (Exception exception)
    {
        if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        app.Logger.LogError(exception, "Unhandled error for {Method} {Path}", context.Request.Method, context.Request.Path);
        throw;
    }
    finally
    {
        app.Logger.LogInformation("{Method} {Path} => {StatusCode} in {ElapsedMs:F1} ms",
            context.Request.Method, context.Request.Path, context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
            app.Logger.LogWarning("401: Authorization header present={HeaderPresent}. Supply a valid Elsa or configured company access token. Check issuer, audience, signature and expiry.",
                context.Request.Headers.ContainsKey("Authorization"));
        if (context.Response.StatusCode == StatusCodes.Status403Forbidden)
            app.Logger.LogWarning("403: Request denied by authorization. Authenticated={Authenticated}; check the account permissions.", context.User.Identity?.IsAuthenticated == true);
    }
});
app.MapWorkflowsApi();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseWorkflows();
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI();
    app.MapGet("/", () => Results.Redirect("/swagger"));
}
await app.RunAsync();









