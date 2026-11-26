using Elsa.Extensions;
using IlWorkflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.json", optional: false);
builder.Configuration.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true);
if (builder.Environment.IsDevelopment()) builder.Configuration.AddUserSecrets<Program>();
builder.Configuration.AddEnvironmentVariables().AddCommandLine(args);
var useConfigurationIdentity = builder.Environment.IsDevelopment() && builder.Configuration.GetValue("Identity:UseConfigurationProviders", true);
if (useConfigurationIdentity) builder.Configuration.AddJsonFile("Samples/identity.json", optional: false, reloadOnChange: true);
var configuration = builder.Configuration;
var swaggerPreview = builder.Environment.IsDevelopment() && configuration.GetValue<bool>("SwaggerPreview");
var connectionString = configuration.GetConnectionString("Elsa")!;
var signingKey = configuration["Identity:Tokens:SigningKey"];
if (swaggerPreview && string.IsNullOrWhiteSpace(signingKey))
    configuration["Identity:Tokens:SigningKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
if (!swaggerPreview && (string.IsNullOrWhiteSpace(signingKey) || signingKey.Length < 32))
    throw new InvalidOperationException("Set Identity:Tokens:SigningKey to a random secret of at least 32 characters using dotnet user-secrets.");
if (!swaggerPreview && builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(configuration["Identity:Admin:Password"]))
    throw new InvalidOperationException("Set Identity:Admin:Password using dotnet user-secrets.");

builder.Services.AddWorkflowServices(configuration, builder.Environment, swaggerPreview, useConfigurationIdentity);

builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "HH:mm:ss ";
    options.IncludeScopes = true;
});

var app = builder.Build();
await app.InitializeDevelopmentDatabaseAsync(connectionString, swaggerPreview);
app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation("Application ready. Storage mode: {StorageMode}", swaggerPreview ? "in memory" : "PostgreSQL; Elsa startup migrations completed"));

// Apply application migrations without touching Elsa's separate migration histories.
// Preview is documentation-only and must work while PostgreSQL is unavailable.
if (!swaggerPreview)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
}
if (app.Environment.IsDevelopment()) app.UseSwaggerSnakeCase();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<WorkflowRequestGuard>();
app.UseWorkflows();
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI();
    app.MapGet("/", () => Results.Redirect("/swagger"));
}
app.MapControllers();
app.MapWorkflowsApi();
await app.RunAsync();
