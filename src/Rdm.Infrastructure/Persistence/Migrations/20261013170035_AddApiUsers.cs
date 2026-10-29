using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rdm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiUsers",
                schema: "rdm",
                columns: table => new
                {
                    Username = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastLoginAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiUsers", x => x.Username);
                });

            foreach (var batch in SqlScripts.Batches("Auth_Procedures.sql"))
                migrationBuilder.Sql(batch);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_ApiUser_Upsert;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_ApiUser_SetLastLogin;");

            migrationBuilder.DropTable(
                name: "ApiUsers",
                schema: "rdm");
        }
    }
}
