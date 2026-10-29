using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rdm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGlAccountAndCostCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CostCenter",
                schema: "md",
                columns: table => new
                {
                    ControllingArea = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ActivationStatus = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostCenter", x => new { x.ControllingArea, x.Code });
                });

            migrationBuilder.CreateTable(
                name: "GlAccount",
                schema: "md",
                columns: table => new
                {
                    CompanyCode = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    AccountNumber = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    AccountCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ChartOfAccounts = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    ShortText = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LongText = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlAccount", x => new { x.CompanyCode, x.AccountNumber });
                });

            migrationBuilder.CreateIndex(
                name: "IX_GlAccount_AccountCode",
                schema: "md",
                table: "GlAccount",
                column: "AccountCode");

            foreach (var batch in SqlScripts.Batches("MasterData_Finance.sql"))
                migrationBuilder.Sql(batch);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var name in new[] { "GlAccount", "CostCenter" })
            {
                migrationBuilder.Sql($"DROP PROCEDURE IF EXISTS md.usp_{name}_Replace;");
                migrationBuilder.Sql($"DROP TYPE IF EXISTS md.{name}TableType;");
            }

            migrationBuilder.DropTable(
                name: "CostCenter",
                schema: "md");

            migrationBuilder.DropTable(
                name: "GlAccount",
                schema: "md");
        }
    }
}
