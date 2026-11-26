using IlWorkflow.Infrastructure.Persistence;
using IlWorkflow.Api.Workflows;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;

using Elsa.Workflows.Activities.Flowchart.Extensions;
using Elsa.Persistence.EFCore.Modules.Identity;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Activities;
using Elsa.UserTasks.Services;
using Elsa.UserTasks.Persistence.EFCore.Extensions;
using Elsa.UserTasks.Persistence.EFCore.PostgreSql.Extensions;
using IlWorkflow.Infrastructure.Identity;
using IlWorkflow.Infrastructure.Persistence.UserTasks;
using IlWorkflow.Infrastructure.Tasks;
using IlWorkflow.Infrastructure.Authentication;

namespace IlWorkflow.API.Extensions;

/// <summary>API composition root: wire application ports to PostgreSQL/Elsa adapters here.
/// Domain and Application never reference this host, EF Core, or Elsa packages.</summary>
public static class WorkflowServiceRegistration
{
    public static IServiceCollection AddWorkflowServices(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment, bool swaggerPreview, bool useConfigurationIdentity)
    {
        var connectionString = configuration.GetConnectionString("Elsa")!;
        services.AddElsa(elsa =>
        {
            elsa.UseIdentity(identity =>
                {
                    identity.TokenOptions += options => configuration.GetSection("Identity:Tokens").Bind(options);
                    if (useConfigurationIdentity)
                    {
                        identity.UseConfigurationBasedUserProvider(options => configuration.GetSection("Identity").Bind(options));
                        identity.UseConfigurationBasedRoleProvider(options => configuration.GetSection("Identity").Bind(options));
                    }
                    // Identity persistence stores local users, password hashes, roles and grants.
                    // User Tasks persistence below stores work items; it is not the user account store.
                    if (!swaggerPreview) identity.UseEntityFrameworkCore(ef => ef.UsePostgreSql(connectionString));
                    // Local admin account is seeded in Development only; production callers use uid tokens.
                    if (!useConfigurationIdentity && environment.IsDevelopment()) identity.UseDefaultAdmin(admin => admin
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
                    // Native tasks, candidate snapshots, operations and audit events survive restart.
                    // Task assignees reference the stable user ID supplied by Elsa Identity.
                    if (!swaggerPreview) tasks.UseEntityFrameworkCore(ef => ef.UsePostgreSql(connectionString));
                })
                .UseFlowchart(flowchart => flowchart.UseTokenBasedExecution())
                .UseJavaScript()
                .AddActivitiesFrom<UserTask>()
                .AddActivitiesFrom<TicketReview>()
                .UseHttp(http => http.ConfigureHttpOptions = options => configuration.GetSection("Http").Bind(options))
                ;
            if (environment.IsDevelopment())
                elsa.AddSwagger();
        });
        
        // Elsa 3.9 defaults capture the root provider; both services resolve scoped runtime/mediator services.
        services.AddScoped<IUserTaskNotificationSink, DefaultUserTaskNotificationSink>();
        if (swaggerPreview) services.AddScoped<IUserTaskWorkflowResumer, DefaultUserTaskWorkflowResumer>();
        // Development accounts come from Elsa's configuration providers, rather than store seeding.
        if (useConfigurationIdentity)
            services.Configure<Elsa.Identity.Options.DefaultAdminUserOptions>(options =>
            {
                options.AdminUserName = configuration["Identity:Admin:UserName"] ?? "admin";
                options.AdminPassword = configuration["Identity:Admin:Password"] ?? "";
            });
        services.AddHttpContextAccessor();
        services.AddUserTokenAuthentication(configuration);
        services.AddScoped<IApproverDirectory, ApproverDirectory>();
        if (!swaggerPreview)
        {
            services.AddSingleton<IUserTaskAccessPolicy, ObservedTaskAccess>();
            services.AddScoped<ObservedUserTasks>();
            services.AddScoped<ParallelTaskCommands>();
            services.AddScoped<DefaultUserTaskWorkflowResumer>();
            services.AddScoped<IUserTaskWorkflowResumer, ParallelTaskWorkflowResumer>();
            services.AddScoped<Elsa.Persistence.EFCore.IEntityModelCreatingHandler, UserTaskPostgreSqlModel>();
            services.AddScoped<IUserTaskRepository, UserTaskNullableJsonRepository>();
        }
        services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString,
            provider => provider.MigrationsHistoryTable("__EFMigrationsHistory", "Application")));
        // FluentValidation handles required fields.
        services.AddControllers(options =>
        {
            options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
        }).AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
            // /api/* request and response properties are snake_case (start_stage, stage_key, or_group...).
            // Dictionary keys (ticket attribute names) and stored snapshots keep their own names.
            options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
        });
        services.AddScoped<WorkflowConfigurationValidator>();
        services.AddScoped<IWorkflowConfigurationService, WorkflowConfigurationService>();
        services.AddSingleton<WorkflowDefinitionBuilder>();
        services.AddScoped<AssignmentResolver>();
        services.AddScoped<WorkflowAccessService>();
        services.AddScoped<TaskService>();
        services.AddSingleton<IUserTaskFormProvider, TicketFormProvider>();
        services.AddAuthorization(options => options.AddPolicy("WorkflowConfigurator", policy =>
            policy.RequireAuthenticatedUser().RequireAssertion(context => PermissionChecks.IsConfigurator(context.User))));
        return services;
    }
}
