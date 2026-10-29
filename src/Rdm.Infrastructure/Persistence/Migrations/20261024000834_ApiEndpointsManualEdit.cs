using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rdm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApiEndpointsManualEdit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                schema: "rdm",
                table: "ApiEndpoints",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            // Sửa tay an toàn: trigger kiểm soát, view xem nhanh, procedure theo mẫu; TVP / procedure thêm Description.
            foreach (var batch in SqlScripts.Batches("ApiEndpoints_ManualEdit.sql"))
                migrationBuilder.Sql(batch);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS rdm.trg_ApiEndpoints_ManualEdit;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS rdm.vw_ApiEndpoints;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_ApiEndpoint_SetActiveByPattern;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_ApiEndpoint_SetActive;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_ApiEndpoint_Sync;");
            migrationBuilder.Sql("DROP TYPE IF EXISTS rdm.ApiEndpointTableType;");
            // Khôi phục kiểu / procedure bản trước (rdm.StringListTableType vẫn còn).
            foreach (var batch in SqlScripts.Batches("ApiEndpoints.sql").Where(b => !b.Contains("StringListTableType AS TABLE")))
                migrationBuilder.Sql(batch);
            migrationBuilder.DropColumn(
                name: "Description",
                schema: "rdm",
                table: "ApiEndpoints");
        }
    }
}
