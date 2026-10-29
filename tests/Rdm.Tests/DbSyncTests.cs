using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Rdm.Application.MasterData;
using Rdm.Infrastructure.DbSync;
using Rdm.Infrastructure.Persistence;

namespace Rdm.Tests;

public class DbSyncTests
{
    private static AppDbContext ModelOnlyContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=.;Database=model_only").Options);

    [Fact]
    public void Every_master_data_table_written_by_repository_is_synced_with_the_right_table_name()
    {
        using var db = ModelOnlyContext();
        var synced = DbSyncTables.All
            .Where(t => t.GetType().IsGenericType)
            .ToDictionary(t => t.GetType().GetGenericArguments()[0], t => t.Name);

        synced.Keys.Should().BeEquivalentTo(MasterDataRepository.Targets.Keys, "bảng md.* mới phải được thêm vào DbSyncTables.All");
        foreach (var (type, name) in synced)
        {
            var entity = db.Model.FindEntityType(type)!;
            $"{entity.GetSchema()}.{entity.GetTableName()}".Should().Be(name, type.Name);
        }
        DbSyncTables.All.Select(t => t.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Api_endpoint_import_columns_match_table_type_in_sql_script()
    {
        var ddl = SqlScripts.Batches("DbSync_Procedures.sql").Single(b => b.Contains("CREATE TYPE rdm.ApiEndpointImportTableType ", StringComparison.Ordinal));
        var columns = Regex.Matches(ddl, @"^\s+(\w+)\s+(nvarchar|bit|datetime2)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();

        columns.Should().Equal(ApiEndpointsTable.Columns);
        SqlScripts.Batches("DbSync_Procedures.sql").Should().Contain(b => b.Contains("PROCEDURE rdm.usp_ApiEndpoint_Import", StringComparison.Ordinal));
    }

    [Fact]
    public void Default_selection_is_master_data_and_api_config_without_user_accounts()
    {
        var names = DbSyncTables.Select(null).Select(t => t.Name).ToList();

        names.Should().Contain("rdm.ApiEndpoints").And.Contain("md.Material").And.NotContain("rdm.ApiUsers");
        names.Count(n => n.StartsWith("md.", StringComparison.Ordinal)).Should().Be(MasterDataRepository.Targets.Count);
    }

    [Theory]
    [InlineData("MD.*", 18)]
    [InlineData("md.Material", 1)]
    [InlineData("md.Material,md.MaterialGroup,rdm.ApiUsers", 3)]
    public void Select_matches_patterns_case_insensitively(string patterns, int expected)
    {
        DbSyncTables.Select(patterns.Split(',')).Should().HaveCount(expected);
    }

    [Fact]
    public void Select_rejects_unknown_table_instead_of_silently_syncing_nothing()
    {
        var act = () => DbSyncTables.Select(new[] { "md.Materal" });

        act.Should().Throw<ArgumentException>().WithMessage("*md.Materal*");
    }

    [Theory]
    [InlineData("Server=localhost,43310;Database=trdm", "Server=127.0.0.1,43310;Database=TRDM", true)]
    [InlineData("Server=tcp:localhost,43310;Database=trdm", "Data Source=localhost,43310;Initial Catalog=trdm", true)]
    [InlineData("Server=localhost,43310;Database=trdm", "Server=localhost,43310;Database=trdm_dev", false)]
    [InlineData("Server=localhost,43310;Database=trdm", "Server=10.72.1.50,1433;Database=trdm", false)]
    public void Same_database_guard(string a, string b, bool same)
    {
        DbSyncService.SameDatabase(a, b).Should().Be(same);
    }

    [Fact]
    public async Task Snapshot_round_trips_through_gzip_file_with_unicode_and_dates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rdm-dbsync-{Guid.NewGuid():N}.json.gz");
        try
        {
            var rows = new List<Material>
            {
                new() { MaterialNumber = "000000000010000001", MaterialCode = "10000001", NameVi = "Thép cuộn cán nóng", BaseUnit = "KG",
                        CreatedOn = new DateOnly(2024, 2, 29), SyncedAtUtc = new DateTime(2026, 10, 23, 18, 0, 12, DateTimeKind.Utc) }
            };
            var snapshot = new DbSnapshot { CreatedAtUtc = DateTime.UtcNow, LastMigration = "20261024010928_AddDbSyncProcedures" };
            snapshot.Tables["md.Material"] = System.Text.Json.JsonSerializer.SerializeToElement(rows);
            snapshot.RowCounts["md.Material"] = rows.Count;

            await DbSyncService.SaveAsync(snapshot, path, CancellationToken.None);
            var loaded = await DbSyncService.LoadAsync(path, CancellationToken.None);

            var back = System.Text.Json.JsonSerializer.Deserialize<List<Material>>(loaded.Tables["md.Material"])!;
            back.Should().BeEquivalentTo(rows);
            loaded.LastMigration.Should().Be(snapshot.LastMigration);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Snapshot_with_row_count_mismatch_is_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rdm-dbsync-{Guid.NewGuid():N}.json");
        try
        {
            var snapshot = new DbSnapshot();
            snapshot.Tables["md.Division"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { new Division { Code = "10" } });
            snapshot.RowCounts["md.Division"] = 2;
            await DbSyncService.SaveAsync(snapshot, path, CancellationToken.None);

            var act = () => DbSyncService.LoadAsync(path, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*md.Division*");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
