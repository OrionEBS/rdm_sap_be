using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Rdm.Application.Abstractions;
using SAP.Middleware.Connector;

namespace Rdm.Sap;

/// <summary>Cấu hình nhật ký gọi SAP (section SapCallLog) — phần tạo JSON trên luồng gọi NCo.</summary>
public sealed class RfcAuditOptions
{
    public const string SectionName = "SapCallLog";

    public bool Enabled { get; set; } = true;
    /// <summary>Số dòng tối đa ghi cho mỗi bảng (TABLES / table EXPORT); bảng dài hơn ghi rowCount + N dòng đầu.</summary>
    public int MaxTableRows { get; set; } = 500;
    /// <summary>Giới hạn riêng theo hàm, VD RFC_READ_TABLE = 20 (đồng bộ master data đọc hàng nghìn dòng / trang).</summary>
    public Dictionary<string, int> TableRowLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase) { ["RFC_READ_TABLE"] = 20 };
    /// <summary>Độ dài tối đa mỗi JSON (ký tự); vượt → chỉ giữ phần đầu (preview).</summary>
    public int MaxJsonChars { get; set; } = 2_000_000;
    /// <summary>Bỏ field giá trị khởi tạo (rỗng, 0, 0000-00-00) — JSON gọn, giữ đúng nội dung thực gửi / nhận.</summary>
    public bool OmitInitialValues { get; set; } = true;
    /// <summary>Hàm không ghi nhật ký (VD RFC_PING).</summary>
    public string[] ExcludeFunctions { get; set; } = Array.Empty<string>();

    internal int RowLimit(string function) =>
        TableRowLimits.TryGetValue(function, out var n) ? Math.Max(0, n) : Math.Max(0, MaxTableRows);

    internal bool IsExcluded(string function) =>
        ExcludeFunctions.Any(f => string.Equals(f, function, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Ghi bằng chứng mọi lời gọi SAP: thay <c>fn.InvokeAudited(dest)</c> bằng <c>fn.InvokeAudited(dest)</c>.
/// Chụp tham số gửi TRƯỚC khi gọi (lời gọi lỗi vẫn có bằng chứng), kết quả SAU khi gọi, thời gian, lỗi → <see cref="SapCallLog.Sink"/>.
/// Chi phí trên luồng gọi = tạo JSON (đã giới hạn số dòng bảng); ghi DB chạy nền theo lô. Lỗi khi tạo nhật ký không bao giờ
/// làm hỏng lời gọi SAP.
/// </summary>
public static class RfcAudit
{
    public static RfcAuditOptions Options { get; set; } = new();

    private static readonly string Machine = Environment.MachineName;

    public static void InvokeAudited(this IRfcFunction fn, RfcDestination dest)
    {
        var sink = SapCallLog.Sink;
        var options = Options;
        string name;
        if (!sink.Enabled || !options.Enabled || options.IsExcluded(name = fn.Metadata.Name))
        {
            fn.Invoke(dest);
            return;
        }

        var started = DateTime.UtcNow;
        var t0 = Stopwatch.GetTimestamp();
        var limit = options.RowLimit(name);
        var (request, requestTruncated) = SafeWrite(() => RfcJson.Write(fn, output: false, limit, options));

        try
        {
            fn.Invoke(dest);
        }
        catch (Exception ex)
        {
            SafeSink(sink, () => new SapCallLogEntry
            {
                StartedAtUtc = started,
                DurationMs = Elapsed(t0),
                FunctionName = name,
                Status = SapCallStatus.Exception,
                ErrorCode = ExceptionCode(ex),
                Message = Cut(ex.Message, 1000),
                Context = SapCallContext.Current,
                Machine = Machine,
                SapSystem = System(dest),
                RequestJson = Cap(request, options),
                ResponseJson = RfcJson.Exception(ex),
                Truncated = requestTruncated
            });
            throw;
        }

        var duration = Elapsed(t0);
        SafeSink(sink, () =>
        {
            var (response, responseTruncated) = RfcJson.Write(fn, output: true, limit, options);
            var outcome = RfcJson.Outcome(fn);
            return new SapCallLogEntry
            {
                StartedAtUtc = started,
                DurationMs = duration,
                FunctionName = name,
                Status = outcome.Status,
                ErrorCode = outcome.Code,
                Message = Cut(outcome.Message, 1000),
                Context = SapCallContext.Current,
                Machine = Machine,
                SapSystem = System(dest),
                RequestJson = Cap(request, options),
                ResponseJson = Cap(response, options),
                Truncated = requestTruncated || responseTruncated
            };
        });
    }

    private static int Elapsed(long t0) => (int)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

    private static string? System(RfcDestination dest)
    {
        try { return $"{dest.SystemID}/{dest.Client}"; }
        catch { return dest.Name; }
    }

    private static (string Json, bool Truncated) SafeWrite(Func<(string, bool)> write)
    {
        try { return write(); }
        catch (Exception ex) { return (JsonSerializer.Serialize(new { auditError = ex.GetType().Name, ex.Message }), false); }
    }

    private static void SafeSink(ISapCallLogSink sink, Func<SapCallLogEntry> build)
    {
        try { sink.Write(build()); }
        catch
        {
            // Nhật ký không bao giờ làm hỏng lời gọi SAP.
        }
    }

    private static string Cap(string json, RfcAuditOptions options) =>
        json.Length <= options.MaxJsonChars
            ? json
            : JsonSerializer.Serialize(new { truncated = true, length = json.Length, preview = json[..Math.Min(4000, json.Length)] });

    internal static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private static string ExceptionCode(Exception ex) => ex switch
    {
        RfcAbapMessageException m => $"{m.GetType().Name}:{m.AbapMessageClass}/{m.AbapMessageNumber}",
        RfcAbapException a when !string.IsNullOrEmpty(a.Key) => $"{a.GetType().Name}:{a.Key}",
        _ => ex.GetType().Name
    };
}

/// <summary>IRfcFunction → JSON theo metadata (không phụ thuộc từng hàm).</summary>
internal static class RfcJson
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>output = false: IMPORT + CHANGING + TABLES (trước khi gọi); true: EXPORT + CHANGING + TABLES (sau khi gọi).</summary>
    public static (string Json, bool Truncated) Write(IRfcFunction fn, bool output, int rowLimit, RfcAuditOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        var truncated = false;
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            var md = fn.Metadata;
            for (var i = 0; i < md.ParameterCount; i++)
            {
                var p = md[i];
                var include = p.Direction switch
                {
                    RfcDirection.IMPORT => !output,
                    RfcDirection.EXPORT => output,
                    _ => true // CHANGING, TABLES
                };
                if (!include) continue;

                switch (p.DataType)
                {
                    case RfcDataType.TABLE:
                    {
                        var table = fn.GetTable(i);
                        if (!output && table.RowCount == 0) continue; // bảng vào rỗng: không gửi gì
                        w.WritePropertyName(p.Name);
                        truncated |= WriteTable(w, table, rowLimit, options);
                        break;
                    }
                    case RfcDataType.STRUCTURE:
                    {
                        var s = fn.GetStructure(i);
                        if (options.OmitInitialValues && IsInitial(s)) continue;
                        w.WritePropertyName(p.Name);
                        WriteStructure(w, s, options);
                        break;
                    }
                    default:
                        if (options.OmitInitialValues && IsInitialValue(fn, i, p.DataType)) continue;
                        w.WritePropertyName(p.Name);
                        WriteValue(w, fn, i, p.DataType);
                        break;
                }
            }
            w.WriteEndObject();
        }
        return (Encoding.UTF8.GetString(buffer.WrittenSpan), truncated);
    }

    private static bool WriteTable(Utf8JsonWriter w, IRfcTable table, int rowLimit, RfcAuditOptions options)
    {
        var rows = table.RowCount;
        var take = Math.Min(rows, rowLimit);
        w.WriteStartObject();
        w.WriteNumber("rowCount", rows);
        if (take < rows) w.WriteBoolean("truncated", true);
        w.WritePropertyName("rows");
        w.WriteStartArray();
        for (var r = 0; r < take; r++) WriteStructure(w, table[r], options);
        w.WriteEndArray();
        w.WriteEndObject();
        return take < rows;
    }

    private static void WriteStructure(Utf8JsonWriter w, IRfcStructure s, RfcAuditOptions options)
    {
        w.WriteStartObject();
        var md = s.Metadata;
        for (var j = 0; j < md.FieldCount; j++)
        {
            var f = md[j];
            switch (f.DataType)
            {
                case RfcDataType.STRUCTURE:
                {
                    var inner = s.GetStructure(j);
                    if (options.OmitInitialValues && IsInitial(inner)) continue;
                    w.WritePropertyName(f.Name);
                    WriteStructure(w, inner, options);
                    break;
                }
                case RfcDataType.TABLE:
                {
                    var inner = s.GetTable(j);
                    if (options.OmitInitialValues && inner.RowCount == 0) continue;
                    w.WritePropertyName(f.Name);
                    WriteTable(w, inner, int.MaxValue, options);
                    break;
                }
                default:
                    if (options.OmitInitialValues && IsInitialValue(s, j, f.DataType)) continue;
                    w.WritePropertyName(f.Name);
                    WriteValue(w, s, j, f.DataType);
                    break;
            }
        }
        w.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter w, IRfcDataContainer c, int index, RfcDataType type)
    {
        switch (type)
        {
            case RfcDataType.BCD:
            case RfcDataType.DECF16:
            case RfcDataType.DECF34:
                w.WriteNumberValue(c.GetDecimal(index));
                break;
            case RfcDataType.FLOAT:
                w.WriteNumberValue(c.GetDouble(index));
                break;
            case RfcDataType.INT1:
            case RfcDataType.INT2:
            case RfcDataType.INT4:
                w.WriteNumberValue(c.GetInt(index));
                break;
            case RfcDataType.INT8:
                w.WriteNumberValue(c.GetLong(index));
                break;
            case RfcDataType.BYTE:
            case RfcDataType.XSTRING:
                var bytes = c.GetByteArray(index) ?? Array.Empty<byte>();
                w.WriteStringValue(bytes.Length <= 512 ? Convert.ToBase64String(bytes) : $"<{bytes.Length} bytes>");
                break;
            default:
                w.WriteStringValue((c.GetString(index) ?? string.Empty).TrimEnd());
                break;
        }
    }

    private static bool IsInitial(IRfcStructure s)
    {
        var md = s.Metadata;
        for (var j = 0; j < md.FieldCount; j++)
        {
            var t = md[j].DataType;
            if (t == RfcDataType.STRUCTURE) { if (!IsInitial(s.GetStructure(j))) return false; }
            else if (t == RfcDataType.TABLE) { if (s.GetTable(j).RowCount > 0) return false; }
            else if (!IsInitialValue(s, j, t)) return false;
        }
        return true;
    }

    private static bool IsInitialValue(IRfcDataContainer c, int index, RfcDataType type)
    {
        switch (type)
        {
            case RfcDataType.BCD:
            case RfcDataType.DECF16:
            case RfcDataType.DECF34:
                return c.GetDecimal(index) == 0m;
            case RfcDataType.FLOAT:
                return c.GetDouble(index) == 0d;
            case RfcDataType.INT1:
            case RfcDataType.INT2:
            case RfcDataType.INT4:
            case RfcDataType.INT8:
                return c.GetLong(index) == 0;
            case RfcDataType.BYTE:
            case RfcDataType.XSTRING:
                var b = c.GetByteArray(index);
                return b is null || b.All(x => x == 0);
            default:
                var s = c.GetString(index);
                if (string.IsNullOrWhiteSpace(s)) return true;
                return type switch
                {
                    RfcDataType.DATE => s is "00000000" or "0000-00-00",
                    RfcDataType.TIME => s is "000000" or "00:00:00",
                    RfcDataType.NUM => s.All(ch => ch == '0'),
                    _ => false
                };
        }
    }

    public static string Exception(Exception ex)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteStartObject("exception");
            w.WriteString("type", ex.GetType().Name);
            w.WriteString("message", RfcAudit.Cut(ex.Message, 4000));
            switch (ex)
            {
                case RfcAbapMessageException m:
                    w.WriteString("messageClass", m.AbapMessageClass);
                    w.WriteString("messageNumber", m.AbapMessageNumber);
                    w.WriteString("messageType", m.AbapMessageType.ToString());
                    break;
                case RfcAbapException a:
                    w.WriteString("key", a.Key);
                    break;
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal readonly record struct CallOutcome(string Status, string? Code, string? Message);

    /// <summary>
    /// Kết quả nghiệp vụ từ bảng / structure message trả về: BAPIRET2 / BAPIRETURN (TYPE, MESSAGE, ID, NUMBER) hoặc
    /// BDCMSGCOLL (MSGTYP, MSGID, MSGNR, MSGV1..4). Có E / A → ERROR; chỉ W → WARNING; còn lại OK.
    /// </summary>
    public static CallOutcome Outcome(IRfcFunction fn)
    {
        (string Code, string? Msg)? firstError = null, firstWarning = null;
        var md = fn.Metadata;
        for (var i = 0; i < md.ParameterCount; i++)
        {
            var p = md[i];
            if (p.Direction == RfcDirection.IMPORT) continue;
            if (p.DataType == RfcDataType.TABLE)
            {
                var t = fn.GetTable(i);
                if (!IsMessageType(t.Metadata.LineType)) continue;
                foreach (IRfcStructure row in t) Inspect(row, ref firstError, ref firstWarning);
            }
            else if (p.DataType == RfcDataType.STRUCTURE)
            {
                var s = fn.GetStructure(i);
                if (IsMessageType(s.Metadata)) Inspect(s, ref firstError, ref firstWarning);
            }
            if (firstError is not null) break;
        }

        if (firstError is { } e) return new CallOutcome(SapCallStatus.Error, e.Code, e.Msg);
        if (firstWarning is { } wn) return new CallOutcome(SapCallStatus.Warning, wn.Code, wn.Msg);
        return new CallOutcome(SapCallStatus.Ok, null, null);
    }

    private static bool IsMessageType(RfcStructureMetadata md) =>
        (Has(md, "TYPE") && Has(md, "MESSAGE")) || (Has(md, "MSGTYP") && Has(md, "MSGID"));

    private static bool Has(RfcStructureMetadata md, string field)
    {
        for (var j = 0; j < md.FieldCount; j++)
            if (md[j].Name == field) return true;
        return false;
    }

    private static void Inspect(IRfcStructure row, ref (string, string?)? firstError, ref (string, string?)? firstWarning)
    {
        var md = row.Metadata;
        string type, code, message;
        if (Has(md, "MSGTYP"))
        {
            type = row.GetString("MSGTYP");
            code = $"{row.GetString("MSGID").Trim()}/{row.GetString("MSGNR").Trim()}";
            message = string.Join(" ", new[] { "MSGV1", "MSGV2", "MSGV3", "MSGV4" }
                .Where(f => Has(md, f)).Select(f => row.GetString(f).Trim()).Where(v => v.Length > 0));
        }
        else
        {
            type = row.GetString("TYPE");
            var id = Has(md, "ID") ? row.GetString("ID").Trim() : Has(md, "CODE") ? row.GetString("CODE").Trim() : string.Empty;
            var number = Has(md, "NUMBER") ? row.GetString("NUMBER").Trim() : string.Empty;
            var parameter = Has(md, "PARAMETER") ? row.GetString("PARAMETER").Trim() : string.Empty;
            code = parameter.Length > 0 && id == "ZRDM" ? parameter : (id.Length > 0 || number.Length > 0 ? $"{id}/{number}" : string.Empty);
            message = row.GetString("MESSAGE").Trim();
        }
        if (type is "E" or "A") firstError ??= (code, message);
        else if (type == "W") firstWarning ??= (code, message);
    }
}
