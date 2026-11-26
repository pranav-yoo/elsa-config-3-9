using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace IlWorkflow.Infrastructure.Persistence;

/// <summary>The attribute catalog is removed for now; configurations always carry an empty attributes list. Down restores the empty table.</summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("202611260001_DropWorkflowAttributes")]
public sealed class DropWorkflowAttributesMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("WorkflowAttributes", "Application");
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("WorkflowAttributes", schema: "Application", columns: table => new
        {
            Id = table.Column<long>(nullable: false).Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
            DefinitionId = table.Column<string>(nullable: false), Key = table.Column<string>(nullable: false),
            Type = table.Column<string>(nullable: false), Required = table.Column<bool>(nullable: false),
            Choices = table.Column<string[]>(type: "text[]", nullable: true), MaxLength = table.Column<int>(nullable: true),
            Minimum = table.Column<decimal>(nullable: true), Maximum = table.Column<decimal>(nullable: true),
            Status = table.Column<string>(nullable: false)
        }, constraints: table => table.PrimaryKey("PK_WorkflowAttributes", x => x.Id));
        migrationBuilder.CreateIndex("IX_WorkflowAttributes_DefinitionId_Key", "WorkflowAttributes", new[] { "DefinitionId", "Key" }, schema: "Application", unique: true);
    }
}
