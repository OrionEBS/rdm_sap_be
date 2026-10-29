using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rdm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiEndpointPublicDocs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DocUpdatedAtUtc",
                schema: "rdm",
                table: "ApiEndpoints",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocUpdatedBy",
                schema: "rdm",
                table: "ApiEndpoints",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicDescription",
                schema: "rdm",
                table: "ApiEndpoints",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PublicHidden",
                schema: "rdm",
                table: "ApiEndpoints",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PublicSummary",
                schema: "rdm",
                table: "ApiEndpoints",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            foreach (var batch in SqlScripts.Batches("ApiEndpoints_PublicDocs.sql"))
                migrationBuilder.Sql(batch);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_ApiEndpoint_SetDoc;");
            foreach (var batch in SqlScripts.Batches("ApiEndpoints_ManualEdit.sql").Where(b => b.Contains("CREATE OR ALTER VIEW")))
                migrationBuilder.Sql(batch);
            migrationBuilder.DropColumn(
                name: "DocUpdatedAtUtc",
                schema: "rdm",
                table: "ApiEndpoints");

            migrationBuilder.DropColumn(
                name: "DocUpdatedBy",
                schema: "rdm",
                table: "ApiEndpoints");

            migrationBuilder.DropColumn(
                name: "PublicDescription",
                schema: "rdm",
                table: "ApiEndpoints");

            migrationBuilder.DropColumn(
                name: "PublicHidden",
                schema: "rdm",
                table: "ApiEndpoints");

            migrationBuilder.DropColumn(
                name: "PublicSummary",
                schema: "rdm",
                table: "ApiEndpoints");
        }
    }
}
