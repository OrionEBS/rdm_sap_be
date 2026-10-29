using System.Text.Json.Serialization;
using Hangfire;
using Rdm.Infrastructure.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Rdm.Api.ApiCatalog;
using Rdm.Api.Auth;
using Rdm.Api.SapCallLogging;
using Rdm.Application.ApiCatalog;
using Rdm.Api.ExceptionHandling;
using Rdm.Application;
using Rdm.Application.Auth;
using Rdm.Infrastructure;
using Rdm.Infrastructure.Jobs;
using Rdm.Infrastructure.Logging;
using Rdm.Infrastructure.Persistence;
using Rdm.Sap;
using Serilog;

// Logger khởi động: ghi cả file — lỗi trước khi cấu hình Serilog chính được nạp (thiếu cấu hình, DB, migration...) vẫn có log.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "logs", "startup-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 10, shared: true)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Configuration.AddRdmSharedConfig();   // RDM_CONFIG_FILE: cấu hình production dùng chung với Rdm.Worker

    // DB là điều kiện bắt buộc (log SQL, migration, Hangfire) — kiểm tra trước để báo lỗi rõ thay cho stack trace của SqlClient.
    Log.Information("Kiểm tra kết nối SQL Server...");
    SqlStartupCheck.EnsureReachable(builder.Configuration.GetConnectionString("SqlServer"));

    builder.Host.UseSerilog((ctx, _, cfg) => cfg.ConfigureRdm(ctx.Configuration, "Rdm.Api"));

    builder.Services
        .AddApplication()
        .AddInfrastructure(builder.Configuration)
        .AddSap(builder.Configuration);
    // Sửa appsettings / RDM_CONFIG_FILE → tự khởi động lại để áp dụng ngay (IIS chạy lại ở request kế tiếp).
    builder.Services.AddConfigChangeRestart(builder.Configuration, builder.Environment);

    // -------- Hangfire (job store = SQL Server, schema hangfire) --------
    // Triển khai IIS: Hangfire:ServerEnabled = false — job chạy ở Rdm.Worker (Windows Service), API chỉ đưa job vào hàng đợi,
    // nên IIS recycle / idle không cắt ngang job. Dev (1 process): mặc định true.
    builder.Services.AddRdmHangfire(builder.Configuration);
    if (builder.Configuration.GetValue("Hangfire:ServerEnabled", true))
        builder.Services.AddHangfireServer(o => o.WorkerCount = builder.Configuration.GetValue("Hangfire:WorkerCount", 2));

    // -------- Chứng thực JWT: mọi endpoint bắt buộc token (trừ [AllowAnonymous]) --------
    builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
    var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
    var signingKey = jwt.CreateKey(); // fail-fast khi thiếu / ngắn khóa ký
    builder.Services.AddSingleton<JwtTokenService>();
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                // Mặc định 5 phút; giảm để token hết hạn đúng Jwt:ExpiryMinutes.
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = JwtRegisteredClaimNames.Sub,
                RoleClaimType = JwtTokenService.RoleClaim
            };
        });
    builder.Services.AddAuthorization(o =>
        o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

    // -------- API --------
    builder.Services
        .AddControllers(o => o.Filters.Add<ApiOutputResultFilter>())   // cấu hình cột output theo API (rdm.ApiOutputProfiles)
        .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    // WriteAsJsonAsync (exception handler) dùng HttpJsonOptions, không dùng options của MVC.
    builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    // CORS cho FE tách riêng (admin-ui React) — chỉ origin khai báo trong Cors:AllowedOrigins; trống = không bật CORS.
    var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
    if (corsOrigins.Length > 0)
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
            .WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

    // Bảng cấu hình active / inactive API (rdm.ApiEndpoints): API inactive ẩn khỏi Swagger + trả 404.
    var apiRegistry = new ApiEndpointRegistry();
    builder.Services.AddSingleton(apiRegistry);
    // Cấu hình cột output (ẩn / đổi tên / thứ tự) + API sao chép /api/c/{tên}/... — nạp cùng bảng cấu hình API.
    var outputRegistry = new ApiOutputRegistry();
    builder.Services.AddSingleton(outputRegistry);
    builder.Services.AddHostedService<ApiEndpointRefreshService>();
    builder.Services.AddSingleton<Rdm.Api.DbSync.DbSyncRunner>();   // nút "Đồng bộ sang DB Dev" (DbSync:TargetConnectionString)

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        // 2 tài liệu (ApiCatalog/ApiDocs.cs): "v1" cho ĐỐI TÁC (mô tả đối tác, không chi tiết SAP) · "internal" NỘI BỘ / TS (đầy đủ).
        // Sinh lại mỗi lần mở Swagger → bật / tắt API, sửa mô tả đối tác có hiệu lực ngay.
        var internalShowInactive = builder.Configuration.GetValue("ApiDocs:InternalShowInactive", false);
        c.DocInclusionPredicate((docName, api) => ApiDocs.Include(docName, api, apiRegistry, internalShowInactive));
        c.SwaggerDoc(ApiDocs.Public, new OpenApiInfo
        {
            Title = builder.Configuration["ApiDocs:PublicTitle"] ?? "RDM Integration API",
            Version = "v1",
            Description = builder.Configuration["ApiDocs:PublicDescription"] ??
                "API tích hợp chứng từ kế toán, bán hàng, bảng giá và danh mục.\n\n" +
                "**Chứng thực:** gọi `POST /api/auth/token` lấy token (hạn 60 phút), bấm **Authorize** và dán token. " +
                "Client gửi header `Authorization: Bearer <token>`; thiếu / hết hạn token → 401.\n\n" +
                "Mọi request ghi bắt buộc có `requestId` (khóa chống trùng): gửi lại requestId đã thành công sẽ trả kết quả cũ, " +
                "không tạo chứng từ mới. HTTP: 200 thành công · 400 dữ liệu sai · 409 requestId đang xử lý · " +
                "422 hệ thống từ chối nghiệp vụ · 502 chưa xác định được kết quả (liên hệ trước khi gửi lại) · 503 hệ thống tạm thời không sẵn sàng."
        });
        c.SwaggerDoc(ApiDocs.Internal, new OpenApiInfo
        {
            Title = "RDM – SAP ECC 6.0 Integration API (NỘI BỘ)",
            Version = "internal",
            Description =
                "**Tài liệu NỘI BỘ** — đầy đủ chi tiết kỹ thuật SAP (BAPI / RFC / bảng), mọi API kể cả đang tắt. Không chia sẻ cho đối tác.\n\n" +
                "Ghi chứng từ sang SAP ECC 6.0 qua NCo 3.1.\n\n" +
                "**Chứng thực:** gọi `POST /api/auth/token` lấy token (hạn 60 phút), bấm **Authorize** và dán token. " +
                "Client gửi header `Authorization: Bearer <token>`; thiếu / hết hạn token → 401.\n\n" +
                "Mọi request ghi bắt buộc có `requestId` (khóa idempotency): gửi lại requestId đã thành công sẽ trả kết quả cũ, " +
                "không tạo chứng từ mới. HTTP: 200 thành công · 400 dữ liệu sai · 409 requestId đang xử lý/Unknown · " +
                "422 SAP báo lỗi nghiệp vụ · 502 Unknown (kiểm tra SAP trước khi gửi lại) · 503 không kết nối được SAP."
        });
        foreach (var xml in new[] { "Rdm.Api.xml", "Rdm.Application.xml" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, xml);
            if (File.Exists(path)) c.IncludeXmlComments(path);
        }

        var bearer = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Name = "Authorization",
            Description = "Dán access token lấy từ POST /api/auth/token (không cần gõ chữ 'Bearer').",
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
        };
        c.AddSecurityDefinition("Bearer", bearer);
        c.AddSecurityRequirement(new OpenApiSecurityRequirement { [bearer] = Array.Empty<string>() });

        // ĐĂNG KÝ CUỐI: chạy sau filter của IncludeXmlComments — nếu không mô tả kỹ thuật SAP từ XML doc ghi đè lại
        // mô tả đối tác trong tài liệu "v1".
        c.OperationFilter<ApiDocsOperationFilter>();
        c.OperationFilter<ApiOutputOperationFilter>();   // response theo cấu hình output
        c.DocumentFilter<ApiOutputDocumentFilter>();     // thêm API sao chép (sau khi mô tả đối tác đã gắn vào API gốc)
        c.DocumentFilter<ApiDocsDocumentFilter>();
    });

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();

        await scope.ServiceProvider.GetRequiredService<AuthService>().EnsureBootstrapAdminAsync(
            app.Configuration["Auth:BootstrapAdmin:Username"], app.Configuration["Auth:BootstrapAdmin:Password"], CancellationToken.None);

        // Đồng bộ danh sách API trong code vào bảng cấu hình (API mới: ApiEndpoints:DefaultActive) rồi nạp trạng thái.
        var endpoints = ApiEndpointRegistry.Discover(app.Services.GetRequiredService<IActionDescriptorCollectionProvider>(),
            Path.Combine(AppContext.BaseDirectory, "Rdm.Api.xml"));
        var endpointRepo = scope.ServiceProvider.GetRequiredService<IApiEndpointRepository>();
        var startedAt = DateTime.UtcNow;
        await endpointRepo.SyncAsync(endpoints, app.Configuration.GetValue("ApiEndpoints:DefaultActive", true), startedAt, CancellationToken.None);
        apiRegistry.StartedAtUtc = startedAt;
        apiRegistry.SetInternalDocKeys(ApiEndpointRegistry.DiscoverInternalDocKeys(app.Services.GetRequiredService<IActionDescriptorCollectionProvider>()));
        apiRegistry.Load(await endpointRepo.GetAllAsync(CancellationToken.None), startedAt);
        outputRegistry.Load(await scope.ServiceProvider.GetRequiredService<IApiOutputProfileRepository>().GetAllAsync(CancellationToken.None));
        Log.Information("Bảng cấu hình API: {Total} API trong code, {Inactive} đang inactive (ẩn Swagger, trả 404)",
            endpoints.Count, apiRegistry.InactiveCount);
    }

    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.UseSerilogRequestLogging();
    ApiOutputRegistry.UseApiOutputCopies(app);   // /api/c/{tên}/... → path API gốc — phải TRƯỚC routing
    app.UseRouting();
    if (corsOrigins.Length > 0) app.UseCors();   // trước authentication: preflight OPTIONS không cần token
    app.UseApiEndpointSwitch();   // API inactive → 404 như URL không tồn tại (trước Swagger / authentication)

    // Swagger + Hangfire dashboard đặt TRƯỚC authorization: FallbackPolicy chặn mọi request đi qua middleware
    // authorization, kể cả trang Swagger (cần mở được để lấy token). Hangfire dashboard có filter riêng: chỉ localhost.
    if (app.Configuration.GetValue("Swagger:Enabled", true))
    {
        // Tài liệu nội bộ (/swagger/internal/swagger.json, /swagger-internal): chỉ từ chính máy chủ hoặc ApiDocs:InternalOpen = true
        // → nơi khác nhận 404 như không tồn tại. Admin tải được qua GET /api/admin/endpoints/export/openapi-internal.
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            if ((path.StartsWithSegments("/swagger/internal") || path.StartsWithSegments("/swagger-internal"))
                && !ApiDocs.InternalAllowed(ctx, app.Configuration))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            // Tài liệu đổi theo bảng cấu hình API (bật / tắt, mô tả) → không cho trình duyệt / proxy / IIS giữ bản cũ.
            if (path.StartsWithSegments("/swagger") || path.StartsWithSegments("/swagger-internal"))
                ctx.Response.OnStarting(() =>
                {
                    ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                    ctx.Response.Headers.Pragma = "no-cache";
                    ctx.Response.Headers.Expires = "0";
                    return Task.CompletedTask;
                });
            await next();
        });
        app.UseSwagger();
        app.UseSwaggerUI(c =>   // /swagger — đối tác
        {
            c.DocumentTitle = app.Configuration["ApiDocs:PublicTitle"] ?? "RDM Integration API";
            c.SwaggerEndpoint($"/swagger/{ApiDocs.Public}/swagger.json", "RDM Integration API v1");
            c.EnablePersistAuthorization();
        });
        app.UseSwaggerUI(c =>   // /swagger-internal — nội bộ
        {
            c.RoutePrefix = "swagger-internal";
            c.DocumentTitle = "RDM SAP Integration API (NỘI BỘ)";
            c.SwaggerEndpoint($"/swagger/{ApiDocs.Internal}/swagger.json", "NỘI BỘ — đầy đủ kỹ thuật SAP");
            c.EnablePersistAuthorization();
        });
    }
    app.UseHangfireDashboard("/hangfire");

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseSapCallContext();   // nhật ký gọi SAP: X-Correlation-Id / user / IP / URL cho mọi lời gọi SAP của request

    RecurringJob.AddOrUpdate<LogRetentionJob>(
        "purge-serilog-events",
        job => job.PurgeSerilogEventsAsync(CancellationToken.None),
        app.Configuration["Jobs:LogRetentionCron"] ?? "0 2 * * *",
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

    RecurringJob.AddOrUpdate<LogRetentionJob>(
        "purge-sap-call-log",
        job => job.PurgeSapCallLogAsync(CancellationToken.None),
        app.Configuration["SapCallLog:RetentionCron"] ?? "30 2 * * *",
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

    RecurringJob.AddOrUpdate<MasterDataSyncJob>(
        "sync-master-data",
        job => job.RunAsync(CancellationToken.None),
        app.Configuration["Jobs:MasterDataSyncCron"] ?? "0 1 * * *",
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

    app.MapControllers();
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription().AllowAnonymous();

    // Kiểm tra sống cho IIS Application Initialization / script triển khai: kết nối DB (không gọi SAP, không cần token).
    app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
        {
            var dbOk = await db.Database.CanConnectAsync(ct);
            var body = new
            {
                status = dbOk ? "Healthy" : "Unhealthy",
                database = dbOk,
                version = typeof(Program).Assembly
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
                machine = Environment.MachineName,
                timeUtc = DateTime.UtcNow
            };
            return dbOk ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        })
        .ExcludeFromDescription().AllowAnonymous();

    await app.RunAsync();
}
catch (StartupCheckException ex)
{
    // Lỗi điều kiện (cấu hình / kết nối): thông báo ngắn, không stack trace, mã thoát 2.
    Log.Fatal("Rdm.Api không khởi động được: {Reason}", ex.Message);
    Environment.ExitCode = 2;
}
catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException
                             || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
{
    // Cổng đang bị tiến trình khác giữ (thường là 1 bản Rdm.Api khác còn chạy) — chỉ báo ngắn, không stack trace.
    Log.Fatal("Rdm.Api không khởi động được: {Reason}. Tắt tiến trình đang giữ cổng (PowerShell: " +
              "Get-NetTCPConnection -State Listen -LocalPort <cổng> | % {{ Get-Process -Id $_.OwningProcess }}) hoặc chạy cổng khác (--urls).",
        ex.Message.TrimEnd('.'));
    Environment.ExitCode = 3;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Rdm.Api dừng do lỗi khởi động");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program;
