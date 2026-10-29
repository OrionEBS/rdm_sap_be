using Microsoft.Data.SqlClient;

namespace Rdm.Infrastructure.Hosting;

/// <summary>Lỗi điều kiện khởi động (cấu hình / kết nối) — in thông báo ngắn gọn, không in stack trace.</summary>
public sealed class StartupCheckException : Exception
{
    public StartupCheckException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Kiểm tra kết nối SQL Server trước khi dựng host (Serilog sink SQL, EF migration, Hangfire đều cần DB) —
/// lỗi trả về thông báo tiếng Việt theo mã lỗi thay cho stack trace của SqlClient.
/// </summary>
public static class SqlStartupCheck
{
    public static void EnsureReachable(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new StartupCheckException("ConnectionStrings:SqlServer chưa cấu hình (appsettings / biến môi trường ConnectionStrings__SqlServer).");

        SqlConnectionStringBuilder csb;
        try
        {
            csb = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex)
        {
            throw new StartupCheckException($"ConnectionStrings:SqlServer sai cú pháp: {ex.Message}", ex);
        }

        var server = csb.DataSource;
        var probe = new SqlConnectionStringBuilder(csb.ConnectionString) { ConnectTimeout = Math.Min(csb.ConnectTimeout, 10), Pooling = false };
        try
        {
            using var conn = new SqlConnection(probe.ConnectionString);
            conn.Open();
        }
        catch (SqlException ex)
        {
            throw new StartupCheckException(Describe(ex, server, csb.InitialCatalog, csb.UserID), ex);
        }
    }

    internal static string Describe(SqlException ex, string server, string database, string user)
    {
        var codes = ex.Errors.Cast<SqlError>().Select(e => e.Number).Append(ex.Number).ToHashSet();
        var reason =
            codes.Contains(10061) ? "bị TỪ CHỐI kết nối (10061): tới được máy nhưng không có SQL Server nghe ở cổng này — sai IP / cổng, " +
                                    "dịch vụ SQL Server chưa chạy, hoặc chưa bật TCP/IP (SQL Server Configuration Manager)" :
            codes.Contains(10060) || codes.Contains(-2) || codes.Contains(258)
                                  ? "HẾT THỜI GIAN chờ (10060): không tới được máy / cổng — firewall chặn, sai IP, hoặc máy SQL không bật" :
            codes.Contains(11001) || codes.Contains(53) || codes.Contains(-1) || codes.Contains(2)
                                  ? "KHÔNG TÌM THẤY máy chủ SQL — sai tên máy / IP, hoặc DNS không phân giải được" :
            codes.Contains(18456) ? $"SAI tài khoản / mật khẩu SQL (18456) cho user '{user}'" :
            codes.Contains(4060)  ? $"KHÔNG MỞ ĐƯỢC database '{database}' (4060): DB chưa tạo hoặc user '{user}' chưa có quyền" :
            codes.Contains(18452) ? "SQL không chấp nhận Windows Authentication của tài khoản này (18452)" :
            codes.Contains(-2146893019) ? "chứng chỉ SQL Server không tin cậy — thêm TrustServerCertificate=True hoặc cài chứng chỉ hợp lệ" :
                                    $"lỗi SQL {ex.Number}: {ex.Message}";
        var local = server.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || server.StartsWith("(local)", StringComparison.OrdinalIgnoreCase)
                    || server.StartsWith("127.0.0.1", StringComparison.Ordinal) || server.StartsWith(".", StringComparison.Ordinal)
            ? $" Lưu ý: '{server}' là SQL trên CHÍNH máy {Environment.MachineName} — máy này không có SQL thì trỏ về máy SQL thật " +
              "(biến môi trường ConnectionStrings__SqlServer hoặc file cấu hình)."
            : string.Empty;
        return $"Không kết nối được SQL Server '{server}', database '{database}': {reason}.{local}";
    }
}
