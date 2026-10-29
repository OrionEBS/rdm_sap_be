using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rdm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMasterDataAndProcedures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "md");

            migrationBuilder.CreateTable(
                name: "CustomerGroup3",
                schema: "md",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    NameVi = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerGroup3", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "PaymentMethod",
                schema: "md",
                columns: table => new
                {
                    Country = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    SapData = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentMethod", x => new { x.Country, x.Code });
                });

            migrationBuilder.CreateTable(
                name: "Plant",
                schema: "md",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    Name1 = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Name2 = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    SalesOrg = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    DistributionChannel = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    Division = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    Street = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    City = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Region = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    SapData = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plant", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "SalesDistrict",
                schema: "md",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    NameVi = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesDistrict", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "SalesOffice",
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
                    table.PrimaryKey("PK_SalesOffice", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "SalesOfficeAssignment",
                schema: "md",
                columns: table => new
                {
                    SalesOrg = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    DistributionChannel = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    Division = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    SalesOffice = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOfficeAssignment", x => new { x.SalesOrg, x.DistributionChannel, x.Division, x.SalesOffice });
                });

            migrationBuilder.CreateTable(
                name: "SalesOrg",
                schema: "md",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: true),
                    CompanyCode = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    SapData = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    NameVi = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrg", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "ShippingCondition",
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
                    table.PrimaryKey("PK_ShippingCondition", x => x.Code);
                });

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

            migrationBuilder.CreateTable(
                name: "SoldTo",
                schema: "md",
                columns: table => new
                {
                    CustomerNumber = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CustomerName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Name1 = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    Name2 = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    Name3 = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    Name4 = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    SearchTerm = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    TaxNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    TaxNumber1 = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    VatRegistrationNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CustomerType = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    CustomerTypeName = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    CreatedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoldTo", x => x.CustomerNumber);
                });

            migrationBuilder.CreateTable(
                name: "SyncRun",
                schema: "md",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Object = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VehicleType",
                schema: "md",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    SapData = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    NameVi = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleType", x => x.Code);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShipTo_ShipToNumber",
                schema: "md",
                table: "ShipTo",
                column: "ShipToNumber");

            migrationBuilder.CreateIndex(
                name: "IX_SoldTo_TaxNumber",
                schema: "md",
                table: "SoldTo",
                column: "TaxNumber");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRun_Object_StartedAtUtc",
                schema: "md",
                table: "SyncRun",
                columns: new[] { "Object", "StartedAtUtc" });

            // Table type + stored procedure: mọi thao tác ghi đi qua procedure.
            foreach (var script in new[] { "MasterData_Procedures.sql", "Posting_Procedures.sql" })
                foreach (var batch in SqlScripts.Batches(script))
                    migrationBuilder.Sql(batch);
        }

        private static readonly string[] Procedures =
        {
            "md.usp_SoldTo_Replace", "md.usp_ShipTo_Replace", "md.usp_SalesDistrict_Replace", "md.usp_ShippingCondition_Replace",
            "md.usp_CustomerGroup3_Replace", "md.usp_SalesOffice_Replace", "md.usp_SalesOfficeAssignment_Replace",
            "md.usp_SalesOrg_Replace", "md.usp_VehicleType_Replace", "md.usp_Plant_Replace", "md.usp_PaymentMethod_Replace",
            "md.usp_SyncRun_Insert", "rdm.usp_SapPosting_TryInsert", "rdm.usp_SapPosting_RestartFailed",
            "rdm.usp_SapPosting_Complete", "dbo.usp_SerilogEvents_Purge"
        };

        private static readonly string[] TableTypes =
        {
            "md.SoldToTableType", "md.ShipToTableType", "md.SalesDistrictTableType", "md.ShippingConditionTableType",
            "md.CustomerGroup3TableType", "md.SalesOfficeTableType", "md.SalesOfficeAssignmentTableType",
            "md.SalesOrgTableType", "md.VehicleTypeTableType", "md.PlantTableType", "md.PaymentMethodTableType"
        };

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var p in Procedures) migrationBuilder.Sql($"DROP PROCEDURE IF EXISTS {p};");
            foreach (var t in TableTypes) migrationBuilder.Sql($"DROP TYPE IF EXISTS {t};");

            migrationBuilder.DropTable(
                name: "CustomerGroup3",
                schema: "md");

            migrationBuilder.DropTable(
                name: "PaymentMethod",
                schema: "md");

            migrationBuilder.DropTable(
                name: "Plant",
                schema: "md");

            migrationBuilder.DropTable(
                name: "SalesDistrict",
                schema: "md");

            migrationBuilder.DropTable(
                name: "SalesOffice",
                schema: "md");

            migrationBuilder.DropTable(
                name: "SalesOfficeAssignment",
                schema: "md");

            migrationBuilder.DropTable(
                name: "SalesOrg",
                schema: "md");

            migrationBuilder.DropTable(
                name: "ShippingCondition",
                schema: "md");

            migrationBuilder.DropTable(
                name: "ShipTo",
                schema: "md");

            migrationBuilder.DropTable(
                name: "SoldTo",
                schema: "md");

            migrationBuilder.DropTable(
                name: "SyncRun",
                schema: "md");

            migrationBuilder.DropTable(
                name: "VehicleType",
                schema: "md");
        }
    }
}
