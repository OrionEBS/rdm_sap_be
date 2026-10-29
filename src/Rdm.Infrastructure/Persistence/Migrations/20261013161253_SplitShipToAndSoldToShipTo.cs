using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rdm.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Tách md.ShipTo (trước đây là quan hệ sold-to/ship-to) thành danh mục ship-to (1 dòng / ship-to)
    /// + bảng quan hệ md.SoldToShipTo. md.ShipTo chỉ là bản sao đồng bộ từ SAP nên drop/create lại;
    /// chạy POST /api/master-data/ship-to/sync và sold-to-ship-to/sync sau khi migrate.
    /// </summary>
    public partial class SplitShipToAndSoldToShipTo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS md.usp_ShipTo_Replace;");
            migrationBuilder.Sql("DROP TYPE IF EXISTS md.ShipToTableType;");

            migrationBuilder.DropTable(name: "ShipTo", schema: "md");

            migrationBuilder.CreateTable(
                name: "ShipTo",
                schema: "md",
                columns: table => new
                {
                    CustomerNumber = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CustomerName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Name1 = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    Name2 = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    Name3 = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    Name4 = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    Street = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    City = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Region = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    SearchTerm = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    TaxNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CustomerType = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    CustomerTypeName = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipTo", x => x.CustomerNumber);
                });

            migrationBuilder.CreateTable(
                name: "SoldToShipTo",
                schema: "md",
                columns: table => new
                {
                    SoldTo = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    SalesOrg = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    DistributionChannel = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    Division = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    PartnerCounter = table.Column<int>(type: "int", nullable: false),
                    ShipTo = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoldToShipTo", x => new { x.SoldTo, x.SalesOrg, x.DistributionChannel, x.Division, x.PartnerCounter });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SoldToShipTo_ShipTo",
                schema: "md",
                table: "SoldToShipTo",
                column: "ShipTo");

            foreach (var batch in SqlScripts.Batches("MasterData_SoldToShipTo.sql"))
                migrationBuilder.Sql(batch);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS md.usp_SoldToShipTo_Replace;");
            migrationBuilder.Sql("DROP TYPE IF EXISTS md.SoldToShipToTableType;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS md.usp_ShipTo_Replace;");
            migrationBuilder.Sql("DROP TYPE IF EXISTS md.ShipToTableType;");

            migrationBuilder.DropTable(name: "SoldToShipTo", schema: "md");
            migrationBuilder.DropTable(name: "ShipTo", schema: "md");

            migrationBuilder.CreateTable(
                name: "ShipTo",
                schema: "md",
                columns: table => new
                {
                    SoldTo = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    SalesOrg = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    DistributionChannel = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    Division = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    PartnerCounter = table.Column<int>(type: "int", nullable: false),
                    ShipToNumber = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    CustomerName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Name1 = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    Name2 = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    Name3 = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    Name4 = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    Street = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    City = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Region = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    TaxNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipTo", x => new { x.SoldTo, x.SalesOrg, x.DistributionChannel, x.Division, x.PartnerCounter });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShipTo_ShipToNumber",
                schema: "md",
                table: "ShipTo",
                column: "ShipToNumber");

            // Khôi phục type + procedure ShipTo phiên bản cũ.
            foreach (var batch in SqlScripts.Batches("MasterData_Procedures.sql")
                         .Where(b => b.Contains("md.ShipToTableType", StringComparison.Ordinal)))
                migrationBuilder.Sql(batch);
        }
    }
}
