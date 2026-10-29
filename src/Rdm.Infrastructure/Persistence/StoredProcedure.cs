using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Rdm.Infrastructure.Persistence;

/// <summary>Gọi stored procedure trên connection của DbContext — mọi thao tác ghi đi qua đây.</summary>
internal static class StoredProcedure
{
    public static async Task<object?> ExecuteScalarAsync(DbContext db, string name, IReadOnlyList<SqlParameter> parameters, CancellationToken ct)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            await using var cmd = new SqlCommand(name, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 };
            foreach (var p in parameters) cmd.Parameters.Add(p);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is DBNull ? null : result;
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    public static SqlParameter P(string name, object? value) => new(name, value ?? DBNull.Value);

    public static SqlParameter Tvp(string name, string typeName, DataTable rows) =>
        new(name, SqlDbType.Structured) { TypeName = typeName, Value = rows };
}
