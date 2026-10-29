using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rdm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "rdm");

            migrationBuilder.CreateTable(
                name: "SapPostings",
                schema: "rdm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RequestId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    RequestJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    DocumentNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CompanyCode = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    FiscalYear = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    ObjectType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    ObjectKey = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ObjectSystem = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReturnMessagesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SapPostings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SapPostings_DocumentNumber",
                schema: "rdm",
                table: "SapPostings",
                column: "DocumentNumber");

            migrationBuilder.CreateIndex(
                name: "IX_SapPostings_Operation_RequestId",
                schema: "rdm",
                table: "SapPostings",
                columns: new[] { "Operation", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SapPostings_Status_CreatedAtUtc",
                schema: "rdm",
                table: "SapPostings",
                columns: new[] { "Status", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SapPostings",
                schema: "rdm");
        }
    }
}
