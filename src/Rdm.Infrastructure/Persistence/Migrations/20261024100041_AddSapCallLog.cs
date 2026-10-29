using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rdm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSapCallLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var batch in SqlScripts.Batches("SapCallLog.sql"))
                migrationBuilder.Sql(batch);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS rdm.vw_SapCallLog;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_SapCallLog_Purge;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_SapCallLog_InsertBatch;");
            migrationBuilder.Sql("DROP TYPE IF EXISTS rdm.SapCallLogTableType;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS rdm.SapCallLog;");
        }
    }
}
