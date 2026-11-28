using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IlWorkflow.Infrastructure.Persistence.Migrations;

// This small explicit migration does not use EnsureCreated, which cannot share an existing Elsa database.
[DbContext(typeof(ApplicationDbContext))]
[Migration("202610310001_ApplicationConfiguration")]
public sealed class ApplicationConfigurationMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("Application");
        migrationBuilder.CreateTable("WorkflowConfigurations", schema: "Application", columns: table => new
        {
            Id = table.Column<long>(nullable: false).Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
            TenantId = table.Column<string>(nullable: false), DefinitionId = table.Column<string>(nullable: false),
            DefinitionVersionId = table.Column<string>(nullable: false), DefinitionVersion = table.Column<int>(nullable: false),
            Revision = table.Column<long>(nullable: false), Frozen = table.Column<bool>(nullable: false), Ready = table.Column<bool>(nullable: false),
            DefinitionHash = table.Column<string>(nullable: false),
            ConfigurationJson = table.Column<string>(type: "jsonb", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_WorkflowConfigurations", x => x.Id));
        migrationBuilder.CreateIndex("IX_WorkflowConfigurations_TenantId_DefinitionVersionId", "WorkflowConfigurations",
            new[] { "TenantId", "DefinitionVersionId" }, schema: "Application", unique: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("WorkflowConfigurations", "Application");
}
