using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rdm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApiEndpointsOutputIntoFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Bỏ trigger (quản trị qua trang /admin → API → procedure). DB đã chạy bản có trigger: gỡ trigger, tạo lại procedure
            // bản không session context (OUTPUT ... INTO) và Sync — đều idempotent.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS rdm.trg_ApiEndpoints_ManualEdit;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_ApiEndpoint_Sync;");
            foreach (var batch in SqlScripts.Batches("ApiEndpoints_ManualEdit.sql")
                         .Where(b => b.Contains("CREATE PROCEDURE rdm.usp_ApiEndpoint_Sync") || b.Contains("CREATE OR ALTER")))
                migrationBuilder.Sql(batch);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Không khôi phục trigger (đã bỏ có chủ đích).
        }
    }
}
