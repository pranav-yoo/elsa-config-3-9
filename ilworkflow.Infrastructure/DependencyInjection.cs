using Elsa.Extensions;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Identity;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Elsa.UserTasks.Activities;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Persistence.EFCore.Extensions;
using Elsa.UserTasks.Persistence.EFCore.PostgreSql.Extensions;
using Elsa.UserTasks.Services;
using Elsa.Workflows.Activities.Flowchart.Extensions;
using IlWorkflow.Infrastructure.Authentication;
using IlWorkflow.Infrastructure.Configuration;
using IlWorkflow.Infrastructure.Identity;
using IlWorkflow.Infrastructure.Persistence;
using IlWorkflow.Infrastructure.Persistence.UserTasks;
using IlWorkflow.Infrastructure.Tasks;
using IlWorkflow.Infrastructure.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace IlWorkflow.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers everything that talks to Elsa or PostgreSQL. Read it top to bottom:
    /// the Elsa engine, our own database, the task API, then identity.
    /// </summary>
    /// <param name="swaggerPreview">Documentation-only mode: Elsa keeps everything in memory and PostgreSQL is not used.</param>
    /// <param name="useConfigurationIdentity">Development: users and roles come from Samples/identity.json instead of the database.</param>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment, bool swaggerPreview, bool useConfigurationIdentity)
    {
        var connectionString = configuration.GetConnectionString("Elsa")!;
        services.AddElsaEngine(configuration, environment, connectionString, swaggerPreview, useConfigurationIdentity);
        services.AddApplicationDatabase(connectionString);
        services.AddTaskServices(swaggerPreview);
        services.AddIdentityAdapters(configuration, useConfigurationIdentity);

        services.AddScoped<IWorkflowConfigurationService, WorkflowConfigurationService>();
        services.AddSingleton<WorkflowDefinitionBuilder>();
        services.AddScoped<WorkflowAccessService>();
        return services;
    }

    /// <summary>The Elsa modules we use. Each Use* call switches on one Elsa feature; storage goes to PostgreSQL unless in preview.</summary>
    private static void AddElsaEngine(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment,
        string connectionString, bool swaggerPreview, bool useConfigurationIdentity)
    {
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
                    // Identity storage holds user accounts, password hashes and roles. User Tasks has its own storage below.
                    if (!swaggerPreview) identity.UseEntityFrameworkCore(ef => ef.UsePostgreSql(connectionString));
                    // A local admin account exists in Development only; production callers use uid tokens.
                    if (!useConfigurationIdentity && environment.IsDevelopment()) identity.UseDefaultAdmin(admin => admin
                        .WithAdminUserName(configuration["Identity:Admin:UserName"] ?? "admin")
                        .WithAdminRoleName("admin")
                        .WithAdminPassword(configuration["Identity:Admin:Password"]!));
                })
                .UseDefaultAuthentication()
                .UseWorkflowManagement(management => { if (!swaggerPreview) management.UseEntityFrameworkCore(ef => ef.UsePostgreSql(connectionString)); })
                .UseWorkflowRuntime(runtime => { if (!swaggerPreview) runtime.UseEntityFrameworkCore(ef => ef.UsePostgreSql(connectionString)); })
                .UseWorkflowsApi()
                // Tasks, candidate lists, revisions and audit events survive a restart.
                .UseUserTasks(tasks => { if (!swaggerPreview) tasks.UseEntityFrameworkCore(ef => ef.UsePostgreSql(connectionString)); })
                .UseFlowchart(flowchart => flowchart.UseTokenBasedExecution())
                .UseJavaScript()
                .AddActivitiesFrom<UserTask>()
                .AddActivitiesFrom<TicketReview>()
                .UseHttp(http => http.ConfigureHttpOptions = options => configuration.GetSection("Http").Bind(options));

            if (environment.IsDevelopment()) elsa.AddSwagger();
        });
    }

    /// <summary>Our own tables (workflow configurations), kept in the "Application" schema with their own migration history.</summary>
    private static void AddApplicationDatabase(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString,
            provider => provider.MigrationsHistoryTable("__EFMigrationsHistory", "Application")));

    /// <summary>Everything around Elsa user tasks: validation of completions, observer lists and parallel-task locking.</summary>
    private static void AddTaskServices(this IServiceCollection services, bool swaggerPreview)
    {
        // Elsa 3.9's defaults capture the root provider, but both services need scoped runtime services.
        services.AddScoped<IUserTaskNotificationSink, DefaultUserTaskNotificationSink>();
        if (swaggerPreview) services.AddScoped<IUserTaskWorkflowResumer, DefaultUserTaskWorkflowResumer>();

        services.AddScoped<TaskService>();
        services.AddSingleton<IUserTaskFormProvider, TicketFormProvider>();
        if (swaggerPreview) return;

        services.AddSingleton<IUserTaskAccessPolicy, ObservedTaskAccess>();
        services.AddScoped<ObservedUserTasks>();
        services.AddScoped<ParallelTaskCommands>();
        services.AddScoped<DefaultUserTaskWorkflowResumer>();
        services.AddScoped<IUserTaskWorkflowResumer, ParallelTaskWorkflowResumer>();
        // Elsa 3.9 PostgreSQL compatibility fixes for the user-task tables.
        services.AddScoped<Elsa.Persistence.EFCore.IEntityModelCreatingHandler, UserTaskPostgreSqlModel>();
        services.AddScoped<IUserTaskRepository, UserTaskNullableJsonRepository>();
    }

    private static void AddIdentityAdapters(this IServiceCollection services, IConfiguration configuration, bool useConfigurationIdentity)
    {
        if (useConfigurationIdentity)
            services.Configure<Elsa.Identity.Options.DefaultAdminUserOptions>(options =>
            {
                options.AdminUserName = configuration["Identity:Admin:UserName"] ?? "admin";
                options.AdminPassword = configuration["Identity:Admin:Password"] ?? "";
            });
        services.AddHttpContextAccessor();
        services.AddUserTokenAuthentication(configuration);
        services.AddScoped<IApproverDirectory, ApproverDirectory>();
    }
}
