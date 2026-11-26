using System.Diagnostics;
using Npgsql;

namespace IlWorkflow.API.Extensions;

public static class DevelopmentDatabaseExtensions
{
    /// <summary>Logs the storage mode and, in Development with PostgreSQL, creates the ilworkflow database when it is missing.</summary>
    public static async Task InitializeDevelopmentDatabaseAsync(this WebApplication app, string connectionString, bool swaggerPreview)
    {
        app.Logger.LogInformation("Storage mode: {StorageMode}", swaggerPreview ? "Swagger preview (in memory)" : "PostgreSQL");
        var databaseSettings = new NpgsqlConnectionStringBuilder(connectionString);
        app.Logger.LogInformation("Database configured: Host={Host}; Port={Port}; Database={Database}; Username={Username}; SSL={SslMode}. Credentials omitted.",
            databaseSettings.Host, databaseSettings.Port, databaseSettings.Database, databaseSettings.Username, databaseSettings.SslMode);
        if (swaggerPreview)
        {
            app.Logger.LogInformation("SwaggerPreview=true: PostgreSQL connections and migrations are skipped. Set SwaggerPreview=false to enable database persistence.");
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
                app.Logger.LogInformation("Database initialization complete. Elsa will apply management and runtime migrations during startup.");
            }
            catch (Exception exception)
            {
                app.Logger.LogError("PostgreSQL initialization failed: {FailureType}; SQLSTATE={SqlState}; Host={Host}; Port={Port}; Database={Database}. Check PostgreSQL readiness, credentials and database permissions.",
                    exception.GetType().Name, (exception as PostgresException)?.SqlState, databaseSettings.Host, databaseSettings.Port, databaseSettings.Database);
                throw;
            }
        }
    }
}
