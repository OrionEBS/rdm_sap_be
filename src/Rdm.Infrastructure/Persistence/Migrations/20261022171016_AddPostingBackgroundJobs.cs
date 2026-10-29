using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rdm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPostingBackgroundJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsBackgroundJob",
                schema: "rdm",
                table: "SapPostings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "JobClaimedAtUtc",
                schema: "rdm",
                table: "SapPostings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressDone",
                schema: "rdm",
                table: "SapPostings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressFailed",
                schema: "rdm",
                table: "SapPostings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressSucceeded",
                schema: "rdm",
                table: "SapPostings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressTotal",
                schema: "rdm",
                table: "SapPostings",
                type: "int",
                nullable: true);

            // TryInsert / RestartFailed thêm @IsBackgroundJob (mặc định 0); ClaimJob, Progress mới.
            foreach (var batch in SqlScripts.Batches("Posting_Jobs.sql"))
                migrationBuilder.Sql(batch);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_SapPosting_ClaimJob;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_SapPosting_Progress;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_SapPosting_TryInsert;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS rdm.usp_SapPosting_RestartFailed;");
            // Khôi phục 2 procedure bản trước (không có cột job).
            foreach (var batch in SqlScripts.Batches("Posting_Procedures.sql")
                         .Where(b => b.Contains("usp_SapPosting_TryInsert") || b.Contains("usp_SapPosting_RestartFailed")))
                migrationBuilder.Sql(batch);

            migrationBuilder.DropColumn(
                name: "IsBackgroundJob",
                schema: "rdm",
                table: "SapPostings");

            migrationBuilder.DropColumn(
                name: "JobClaimedAtUtc",
                schema: "rdm",
                table: "SapPostings");

            migrationBuilder.DropColumn(
                name: "ProgressDone",
                schema: "rdm",
                table: "SapPostings");

            migrationBuilder.DropColumn(
                name: "ProgressFailed",
                schema: "rdm",
                table: "SapPostings");

            migrationBuilder.DropColumn(
                name: "ProgressSucceeded",
                schema: "rdm",
                table: "SapPostings");

            migrationBuilder.DropColumn(
                name: "ProgressTotal",
                schema: "rdm",
                table: "SapPostings");
        }
    }
}
