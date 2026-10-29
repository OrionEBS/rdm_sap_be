using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rdm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiEndpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiEndpoints",
                schema: "rdm",
                columns: table => new
                {
                    EndpointKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    HttpMethod = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    RouteTemplate = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    GroupName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ActionName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IsLocked = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiEndpoints", x => x.EndpointKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiEndpoints_GroupName",
                schema: "rdm",
                table: "ApiEndpoints",
                column: "GroupName");

            foreach (var batch in SqlScripts.Batches("ApiEndpoints.sql"))
                migrationBuilder.Sql(batch);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_ApiEndpoint_SetActive;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_ApiEndpoint_Sync;");
            migrationBuilder.Sql("DROP TYPE IF EXISTS rdm.StringListTableType;");
            migrationBuilder.Sql("DROP TYPE IF EXISTS rdm.ApiEndpointTableType;");
            migrationBuilder.DropTable(
                name: "ApiEndpoints",
                schema: "rdm");
        }
    }
}
