using System.Diagnostics;
using IlWorkflow.Infrastructure.Configuration;
using IlWorkflow.Infrastructure.Persistence;
using IlWorkflow.Infrastructure.Authorization;
using IlWorkflow.Api.Workflows;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
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

namespace IlWorkflow.API.Services;

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
                    // Native tasks, candidate snapshots, operations and audit events survive restart.
                    // Task assignees reference the stable user ID supplied by Elsa Identity/external authentication.
                    if (!swaggerPreview) tasks.UseEntityFrameworkCore(ef => ef.UsePostgreSql(connectionString));
                })
                .UseFlowchart(flowchart => flowchart.UseTokenBasedExecution())
                .UseJavaScript()
                .AddActivitiesFrom<UserTask>()
                .AddActivitiesFrom<TicketFlow>()
                .AddActivitiesFrom<TicketReview>()
                .UseHttp(http => http.ConfigureHttpOptions = options => configuration.GetSection("Http").Bind(options))
                ;
            if (environment.IsDevelopment())
                elsa.AddSwagger();
        });
        
        // Elsa 3.9 defaults capture the root provider; both services resolve scoped runtime/mediator services.
        services.AddScoped<IUserTaskNotificationSink, DefaultUserTaskNotificationSink>();
        services.AddScoped<IUserTaskWorkflowResumer, DefaultUserTaskWorkflowResumer>();
        services.AddSingleton<IUserTaskFormProvider, IlWorkflow.Api.Tickets.TicketFormProvider>();
        // Development accounts come from Elsa's configuration providers, rather than store seeding.
        if (useConfigurationIdentity)
            services.Configure<Elsa.Identity.Options.DefaultAdminUserOptions>(options =>
            {
                options.AdminUserName = configuration["Identity:Admin:UserName"] ?? "admin";
                options.AdminPassword = configuration["Identity:Admin:Password"] ?? "";
            });
        
        
        
        
        services.AddHttpContextAccessor();
        services.AddCompanyAuthentication(configuration, environment);
        services.AddScoped<IApproverDirectory, TicketIdentity>();
        if (!swaggerPreview)
        {
            services.AddScoped<Elsa.Persistence.EFCore.IEntityModelCreatingHandler, UserTaskPostgreSqlModel>();
            services.AddScoped<IUserTaskRepository, UserTaskNullableJsonRepository>();
        }
        services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString,
            provider => provider.MigrationsHistoryTable("__EFMigrationsHistory", "Application")));
        services.AddControllers().AddJsonOptions(options =>
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
        services.AddScoped<WorkflowConfigurationValidator>();
        services.AddScoped<IWorkflowConfigurationService, WorkflowConfigurationService>();
        services.AddSingleton<WorkflowDefinitionBuilder>();
        services.AddScoped<AssignmentResolver>();
        services.AddScoped<WorkflowAccessService>();
        services.AddSingleton<IUserTaskFormProvider, IlWorkflow.Api.Workflows.TicketFormProvider>();
        services.AddAuthorization(options => options.AddPolicy("WorkflowConfigurator", policy =>
            policy.RequireAuthenticatedUser().RequireAssertion(context => ConfigurationAccess.IsConfigurator(context.User))));
        
        return services;
    }
}
