using System.Diagnostics;
using FluentAssertions;
using Rdm.Sap;
using SAP.Middleware.Connector;
using Xunit.Abstractions;

namespace Rdm.Tests;

/// <summary>Chi phí tạo JSON nhật ký trên luồng gọi SAP (phần duy nhất không chạy nền).</summary>
public class SapCallLogPerfTests(ITestOutputHelper output)
{
    private static IRfcFunction Function(int rows)
    {
        var line = new RfcStructureMetadata("ZLINE");
        int nuc = 0, uc = 0;
        foreach (var (name, len) in new[] { ("KNUMH", 10), ("KSCHL", 4), ("VKORG", 4), ("KUNAG", 10), ("MATNR", 18), ("WERKS", 4), ("TEXT", 60) })
        { line.AddField(new RfcFieldMetadata(name, RfcDataType.CHAR, len, nuc, len * 2, uc)); nuc += len; uc += len * 2; }
        line.AddField(new RfcFieldMetadata("DATAB", RfcDataType.DATE, 8, nuc, 16, uc)); nuc += 8; uc += 16;
        line.AddField(new RfcFieldMetadata("KBETR", RfcDataType.BCD, 12, nuc, 12, uc, 4));
        line.Lock();
        var fmd = new RfcFunctionMetadata("Z_PERF");
        fmd.AddParameter(new RfcParameterMetadata("TI_ROWS", new RfcTableMetadata("ZLINE_T", line), RfcDirection.TABLES, true));
        fmd.Lock();
        var fn = fmd.CreateFunction();
        var t = fn.GetTable("TI_ROWS");
        for (var i = 0; i < rows; i++)
        {
            t.Append();
            t.SetValue("KNUMH", (119000 + i).ToString("0000000000")); t.SetValue("KSCHL", "ZVLP"); t.SetValue("VKORG", "G210");
            t.SetValue("KUNAG", "0000100166"); t.SetValue("MATNR", "000000000000100026"); t.SetValue("WERKS", "G211");
            t.SetValue("TEXT", "Thép cuộn cán nóng loại 1"); t.SetValue("DATAB", "20270101"); t.SetValue("KBETR", 1650000m + i);
        }
        return fn;
    }

    [Theory]
    [InlineData(50, 50)]      // 1 lô bảng giá Z_ESD016 (Sap:PriceBapiChunkSize)
    [InlineData(500, 500)]    // bảng lớn nhất được ghi đủ (SapCallLog:MaxTableRows)
    [InlineData(5000, 20)]    // 1 trang RFC_READ_TABLE khi đồng bộ master data (chỉ ghi 20 dòng)
    public void Json_cost_on_calling_thread_is_small(int rows, int limit)
    {
        var fn = Function(rows);
        var options = new RfcAuditOptions();
        for (var i = 0; i < 20; i++) RfcJson.Write(fn, false, limit, options); // làm nóng JIT

        const int n = 200;
        var sw = Stopwatch.StartNew();
        var chars = 0;
        for (var i = 0; i < n; i++) chars = RfcJson.Write(fn, false, limit, options).Json.Length;
        sw.Stop();
        var perCall = sw.Elapsed.TotalMilliseconds / n;
        output.WriteLine($"{rows} dòng (ghi {Math.Min(rows, limit)}): {perCall:N3} ms / lời gọi, JSON {chars:N0} ký tự");

        perCall.Should().BeLessThan(rows >= 500 && limit >= 500 ? 20 : 5);
    }
}
