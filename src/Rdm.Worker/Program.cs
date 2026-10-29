using Hangfire;
using Rdm.Application;
using Rdm.Infrastructure;
using Rdm.Infrastructure.Hosting;
using Rdm.Infrastructure.Logging;
using Rdm.Sap;
using Serilog;

// Rdm.Worker — Windows Service chạy Hangfire server: job bảng giá hàng loạt (/api/sd/prices-bapi/jobs),
// đồng bộ master data, dọn log. Rdm.Api trên IIS chỉ đưa job vào hàng đợi (Hangfire:ServerEnabled = false),
// nên IIS recycle / idle không cắt ngang job. Không chạy EF migration (Rdm.Api làm khi khởi động).

// Logger khởi động: ghi cả file — lỗi trước khi cấu hình Serilog chính được nạp (thiếu cấu hình, DB, migration...) vẫn có log.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "logs", "startup-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 10, shared: true)
    .CreateBootstrapLogger();

try
{
    // Windows Service chạy với thư mục hiện hành = System32 → content root phải là thư mục chương trình.
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    });
    builder.Configuration.AddRdmSharedConfig();

    // DB chưa sẵn sàng (VD SQL khởi động chậm sau reboot) → thoát mã 2, recovery của service tự khởi động lại sau 60 giây.
    Log.Information("Kiểm tra kết nối SQL Server...");
    SqlStartupCheck.EnsureReachable(builder.Configuration.GetConnectionString("SqlServer"));

    builder.Services.AddWindowsService(o => o.ServiceName = "RdmWorker");
    // Sửa cấu hình → thoát mã 3 → recovery của service khởi động lại với cấu hình mới.
    builder.Services.AddConfigChangeRestart(builder.Configuration, builder.Environment);
    builder.Services.AddSerilog((_, cfg) => cfg.ConfigureRdm(builder.Configuration, "Rdm.Worker"));

    builder.Services
        .AddApplication()
        .AddInfrastructure(builder.Configuration)
        .AddSap(builder.Configuration);

    builder.Services.AddRdmHangfire(builder.Configuration);
    builder.Services.AddHangfireServer(o =>
    {
        o.WorkerCount = builder.Configuration.GetValue("Hangfire:WorkerCount", 2);
        o.ServerName = $"{Environment.MachineName}:rdm-worker";
    });

    // Dừng service: chờ job đang chạy tối đa ShutdownTimeoutSeconds. Job tạo / sửa bảng giá bị cắt ngang → Unknown (không chạy lại).
    builder.Services.Configure<HostOptions>(o =>
        o.ShutdownTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Hangfire:ShutdownTimeoutSeconds", 120)));

    var host = builder.Build();
    Log.Information("Rdm.Worker khởi động ({Environment}, {Machine})", builder.Environment.EnvironmentName, Environment.MachineName);
    await host.RunAsync();
}
catch (StartupCheckException ex)
{
    Log.Fatal("Rdm.Worker không khởi động được: {Reason}", ex.Message);
    Environment.ExitCode = 2;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Rdm.Worker dừng do lỗi khởi động");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
