using System.Collections.ObjectModel;
using System.Data;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Sinks.MSSqlServer;

namespace Rdm.Infrastructure.Logging;

public static class SerilogConfiguration
{
    public const string TableName = "SerilogEvents";
    public const string TableSchema = "dbo";

    /// <summary>Sinks: Console, File rolling theo ngày, MSSqlServer (dbo.SerilogEvents, tự tạo bảng).</summary>
    public static LoggerConfiguration ConfigureRdm(this LoggerConfiguration cfg, IConfiguration config, string application = "Rdm.Api")
    {
        cfg.ReadFrom.Configuration(config)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", application)
            .WriteTo.Console()
            .WriteTo.File(
                path: Path.Combine(AppContext.BaseDirectory, "logs", "rdm-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true);

        var cs = config.GetConnectionString("SqlServer");
        if (string.IsNullOrWhiteSpace(cs)) return cfg;
        // AutoCreateSqlTable kết nối DB ngay khi tạo sink: lỗi (DB không tới được / thiếu quyền tạo bảng) chỉ tắt log SQL,
        // không làm sập ứng dụng — log file vẫn chạy; lỗi kết nối thật được SqlStartupCheck / migration báo rõ.
        try
        {
            cfg.WriteTo.MSSqlServer(
                cs,
                sinkOptions: new MSSqlServerSinkOptions
                {
                    TableName = TableName,
                    SchemaName = TableSchema,
                    AutoCreateSqlTable = true
                },
                columnOptions: new ColumnOptions
                {
                    AdditionalColumns = new Collection<SqlColumn>
                    {
                        new() { ColumnName = "Operation", DataType = SqlDbType.NVarChar, DataLength = 32, AllowNull = true },
                        new() { ColumnName = "RequestId", DataType = SqlDbType.NVarChar, DataLength = 64, AllowNull = true },
                        new() { ColumnName = "BapiName", DataType = SqlDbType.NVarChar, DataLength = 50, AllowNull = true }
                    }
                },
                restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WRN] Không bật được log vào SQL ({TableSchema}.{TableName}): {ex.GetBaseException().Message} — chỉ ghi log file.");
        }

        return cfg;
    }
}
