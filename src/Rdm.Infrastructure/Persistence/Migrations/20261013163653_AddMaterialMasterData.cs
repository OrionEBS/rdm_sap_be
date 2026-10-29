using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rdm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMaterialMasterData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Division",
                schema: "md",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    NameVi = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Division", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "Material",
                schema: "md",
                columns: table => new
                {
                    MaterialNumber = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    MaterialCode = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    NameVi = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    BaseUnit = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    BaseUnitExternal = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    BaseUnitNameEn = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    BaseUnitNameVi = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    MaterialType = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    MaterialGroup = table.Column<string>(type: "nvarchar(9)", maxLength: 9, nullable: true),
                    Division = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ChangedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Material", x => x.MaterialNumber);
                });

            migrationBuilder.CreateTable(
                name: "MaterialGroup",
                schema: "md",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(9)", maxLength: 9, nullable: false),
                    LongNameEn = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    LongNameVi = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    SapData = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    NameVi = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialGroup", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "MaterialType",
                schema: "md",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    SapData = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    NameVi = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialType", x => x.Code);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Material_MaterialCode",
                schema: "md",
                table: "Material",
                column: "MaterialCode");

            migrationBuilder.CreateIndex(
                name: "IX_Material_MaterialGroup",
                schema: "md",
                table: "Material",
                column: "MaterialGroup");

            migrationBuilder.CreateIndex(
                name: "IX_Material_MaterialType",
                schema: "md",
                table: "Material",
                column: "MaterialType");

            // Table type + stored procedure ghi dữ liệu.
            foreach (var batch in SqlScripts.Batches("MasterData_Material.sql"))
                migrationBuilder.Sql(batch);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var name in new[] { "Material", "MaterialGroup", "MaterialType", "Division" })
            {
                migrationBuilder.Sql($"DROP PROCEDURE IF EXISTS md.usp_{name}_Replace;");
                migrationBuilder.Sql($"DROP TYPE IF EXISTS md.{name}TableType;");
            }

            migrationBuilder.DropTable(
                name: "Division",
                schema: "md");

            migrationBuilder.DropTable(
                name: "Material",
                schema: "md");

            migrationBuilder.DropTable(
                name: "MaterialGroup",
                schema: "md");

            migrationBuilder.DropTable(
                name: "MaterialType",
                schema: "md");
        }
    }
}
