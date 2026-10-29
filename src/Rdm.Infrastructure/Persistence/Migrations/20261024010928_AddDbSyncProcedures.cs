using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rdm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDbSyncProcedures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var batch in SqlScripts.Batches("DbSync_Procedures.sql"))
                migrationBuilder.Sql(batch);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_ApiEndpoint_Import;");
            migrationBuilder.Sql("DROP TYPE IF EXISTS rdm.ApiEndpointImportTableType;");
        }
    }
}
