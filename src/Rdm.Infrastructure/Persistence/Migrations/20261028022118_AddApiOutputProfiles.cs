using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rdm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiOutputProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiOutputProfiles",
                schema: "rdm",
                columns: table => new
                {
                    ProfileId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EndpointKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CopyName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    FieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiOutputProfiles", x => x.ProfileId);
                });

            migrationBuilder.CreateIndex(
                name: "UX_ApiOutputProfiles_CopyName",
                schema: "rdm",
                table: "ApiOutputProfiles",
                column: "CopyName",
                unique: true,
                filter: "[CopyName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_ApiOutputProfiles_Original",
                schema: "rdm",
                table: "ApiOutputProfiles",
                column: "EndpointKey",
                unique: true,
                filter: "[CopyName] IS NULL");

            foreach (var batch in SqlScripts.Batches("ApiOutputProfiles.sql"))
                migrationBuilder.Sql(batch);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS rdm.vw_ApiOutputProfiles;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_ApiOutputProfile_Import;");
            migrationBuilder.Sql("DROP TYPE IF EXISTS rdm.ApiOutputProfileImportTableType;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_ApiOutputProfile_Delete;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_ApiOutputProfile_Save;");
            migrationBuilder.DropTable(
                name: "ApiOutputProfiles",
                schema: "rdm");
        }
    }
}
