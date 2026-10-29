using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Rdm.Api.SapCallLogging;
using Rdm.Application.Abstractions;
using Rdm.Infrastructure.Persistence;
using Rdm.Infrastructure.SapCallLogging;
using Rdm.Sap;
using SAP.Middleware.Connector;

namespace Rdm.Tests;

public class SapCallLogTests
{
    // ------------------------------------------------------------------ metadata NCo dựng offline (không cần SAP)

    private sealed class StructBuilder(string name)
    {
        private readonly RfcStructureMetadata _md = new(name);
        private int _nuc, _uc;

        public StructBuilder Char(string field, int len) => Add(field, RfcDataType.CHAR, len, len * 2);
        public StructBuilder Num(string field, int len) => Add(field, RfcDataType.NUM, len, len * 2);
        public StructBuilder Date(string field) => Add(field, RfcDataType.DATE, 8, 16);
        public StructBuilder Bcd(string field, int bytes, int decimals) => Add(field, RfcDataType.BCD, bytes, bytes, decimals);

        private StructBuilder Add(string field, RfcDataType type, int nuc, int uc, int decimals = 0)
        {
            _md.AddField(new RfcFieldMetadata(field, type, nuc, _nuc, uc, _uc, decimals));
            _nuc += nuc;
            _uc += uc;
            return this;
        }

        public RfcStructureMetadata Build()
        {
            _md.Lock();
            return _md;
        }
    }

    private static RfcStructureMetadata Bapiret2() =>
        new StructBuilder("BAPIRET2").Char("TYPE", 1).Char("ID", 20).Num("NUMBER", 3).Char("MESSAGE", 220).Char("PARAMETER", 32).Build();

    private static IRfcFunction TestFunction()
    {
        var head = new StructBuilder("ZHEAD").Char("BUKRS", 4).Date("BLDAT").Bcd("AMOUNT", 12, 4).Char("EMPTY", 10).Build();
        var item = new StructBuilder("ZITEM").Num("POSNR", 3).Char("MATNR", 18).Build();
        var fmd = new RfcFunctionMetadata("Z_TEST_AUDIT");
        fmd.AddParameter(new RfcParameterMetadata("IV_KUNNR", RfcDataType.CHAR, 10, 20, RfcDirection.IMPORT, true));
        fmd.AddParameter(new RfcParameterMetadata("IV_EMPTY", RfcDataType.CHAR, 10, 20, RfcDirection.IMPORT, true));
        fmd.AddParameter(new RfcParameterMetadata("IS_HEAD", head, RfcDirection.IMPORT, true));
        fmd.AddParameter(new RfcParameterMetadata("EV_DOC", RfcDataType.CHAR, 10, 20, RfcDirection.EXPORT, true));
        fmd.AddParameter(new RfcParameterMetadata("IT_ITEMS", new RfcTableMetadata("ZITEM_T", item), RfcDirection.TABLES, true));
        fmd.AddParameter(new RfcParameterMetadata("ET_RETURN", new RfcTableMetadata("BAPIRET2_T", Bapiret2()), RfcDirection.TABLES, true));
        fmd.Lock();
        return fmd.CreateFunction();
    }

    private static RfcAuditOptions Options(int maxRows = 5) => new() { MaxTableRows = maxRows };

    [Fact]
    public void Request_json_has_sent_values_only_and_truncates_long_tables()
    {
        var fn = TestFunction();
        fn.SetValue("IV_KUNNR", "0000100166");
        var head = fn.GetStructure("IS_HEAD");
        head.SetValue("BUKRS", "G210");
        head.SetValue("BLDAT", "20261024");
        head.SetValue("AMOUNT", 1650000.5m);
        var items = fn.GetTable("IT_ITEMS");
        for (var i = 1; i <= 30; i++) { items.Append(); items.SetValue("POSNR", i.ToString("000")); items.SetValue("MATNR", $"MAT{i}"); }

        var (json, truncated) = RfcJson.Write(fn, output: false, rowLimit: 5, Options());
        var doc = JsonDocument.Parse(json).RootElement;

        truncated.Should().BeTrue();
        doc.GetProperty("IV_KUNNR").GetString().Should().Be("0000100166");
        doc.TryGetProperty("IV_EMPTY", out _).Should().BeFalse("giá trị khởi tạo không được gửi");
        doc.TryGetProperty("EV_DOC", out _).Should().BeFalse("EXPORT không thuộc request");
        doc.TryGetProperty("ET_RETURN", out _).Should().BeFalse("bảng vào rỗng");
        doc.GetProperty("IS_HEAD").GetProperty("BUKRS").GetString().Should().Be("G210");
        doc.GetProperty("IS_HEAD").GetProperty("BLDAT").GetString().Should().Be("2026-10-24");
        doc.GetProperty("IS_HEAD").GetProperty("AMOUNT").GetDecimal().Should().Be(1650000.5m);
        doc.GetProperty("IS_HEAD").TryGetProperty("EMPTY", out _).Should().BeFalse();
        var t = doc.GetProperty("IT_ITEMS");
        t.GetProperty("rowCount").GetInt32().Should().Be(30);
        t.GetProperty("truncated").GetBoolean().Should().BeTrue();
        t.GetProperty("rows").GetArrayLength().Should().Be(5);
        t.GetProperty("rows")[0].GetProperty("MATNR").GetString().Should().Be("MAT1");
    }

    [Fact]
    public void Response_json_has_exports_and_tables_and_vietnamese_is_not_escaped()
    {
        var fn = TestFunction();
        fn.SetValue("EV_DOC", "1400000123");
        var ret = fn.GetTable("ET_RETURN");
        ret.Append();
        ret.SetValue("TYPE", "S");
        ret.SetValue("MESSAGE", "Đã tạo chứng từ 1400000123");

        var (json, truncated) = RfcJson.Write(fn, output: true, rowLimit: 5, Options());

        truncated.Should().BeFalse();
        json.Should().Contain("Đã tạo chứng từ");
        var doc = JsonDocument.Parse(json).RootElement;
        doc.GetProperty("EV_DOC").GetString().Should().Be("1400000123");
        doc.TryGetProperty("IV_KUNNR", out _).Should().BeFalse();
        doc.GetProperty("ET_RETURN").GetProperty("rowCount").GetInt32().Should().Be(1);
    }

    [Theory]
    [InlineData("S", "", "OK", null)]
    [InlineData("W", "", "WARNING", "/000")]
    [InlineData("E", "", "ERROR", "/000")]
    [InlineData("A", "", "ERROR", "/000")]
    [InlineData("E", "CREDIT_CUSTOMER_NOT_FOUND", "ERROR", "CREDIT_CUSTOMER_NOT_FOUND")]
    public void Outcome_comes_from_return_messages(string type, string parameter, string status, string? code)
    {
        var fn = TestFunction();
        var ret = fn.GetTable("ET_RETURN");
        ret.Append();
        ret.SetValue("TYPE", type);
        ret.SetValue("ID", parameter.Length > 0 ? "ZRDM" : "");
        ret.SetValue("PARAMETER", parameter);
        ret.SetValue("MESSAGE", "Thông báo");

        var o = RfcJson.Outcome(fn);

        o.Status.Should().Be(status);
        if (code is null) o.Code.Should().BeNull();
        else o.Code.Should().EndWith(code);
        if (status != "OK") o.Message.Should().Be("Thông báo");
    }

    [Fact]
    public void Error_is_reported_even_when_a_success_message_comes_first()
    {
        var fn = TestFunction();
        var ret = fn.GetTable("ET_RETURN");
        ret.Append(); ret.SetValue("TYPE", "S"); ret.SetValue("MESSAGE", "ok");
        ret.Append(); ret.SetValue("TYPE", "W"); ret.SetValue("MESSAGE", "cảnh báo");
        ret.Append(); ret.SetValue("TYPE", "E"); ret.SetValue("ID", "F5"); ret.SetValue("NUMBER", "702"); ret.SetValue("MESSAGE", "Kỳ kế toán đóng");

        var o = RfcJson.Outcome(fn);

        o.Status.Should().Be(SapCallStatus.Error);
        o.Code.Should().Be("F5/702");
        o.Message.Should().Be("Kỳ kế toán đóng");
    }

    [Fact]
    public void Exception_json_keeps_type_and_message()
    {
        var json = RfcJson.Exception(new InvalidOperationException("mất kết nối"));
        var e = JsonDocument.Parse(json).RootElement.GetProperty("exception");

        e.GetProperty("type").GetString().Should().Be("InvalidOperationException");
        e.GetProperty("message").GetString().Should().Be("mất kết nối");
    }

    [Fact]
    public void Row_limit_per_function_overrides_default()
    {
        var o = new RfcAuditOptions { MaxTableRows = 500 };

        o.RowLimit("RFC_READ_TABLE").Should().Be(20);
        o.RowLimit("rfc_read_table").Should().Be(20);
        o.RowLimit("Z_ESD016_PRICES_CONDITIONS").Should().Be(500);
    }

    // ------------------------------------------------------------------ ngữ cảnh

    [Fact]
    public async Task Context_flows_into_nco_thread_and_operation_scope_keeps_http_data()
    {
        using (SapCallContext.Begin(new SapCallContext { Source = "http", CorrelationId = "EU-123", UserName = "eu.user" }))
        using (SapCallContext.BeginOperation("PriceBapiCreate", "REQ-1", Guid.Empty))
        {
            var seen = await Task.Run(() => SapCallContext.Current);
            seen!.CorrelationId.Should().Be("EU-123");
            seen.UserName.Should().Be("eu.user");
            seen.Operation.Should().Be("PriceBapiCreate");
            seen.RequestId.Should().Be("REQ-1");
            seen.Source.Should().Be("http");
        }
        SapCallContext.Current.Should().BeNull();
    }

    [Theory]
    [InlineData("EU-2026-10-24-000123", "EU-2026-10-24-000123")]
    [InlineData("  abc:def_1.2  ", "abc:def_1.2")]
    [InlineData("<script>", null)]
    [InlineData("a b", null)]
    [InlineData("", null)]
    public void Correlation_header_is_sanitized(string raw, string? expected) =>
        SapCallContextMiddleware.Clean(raw).Should().Be(expected);

    [Fact]
    public void Correlation_header_longer_than_100_chars_is_rejected() =>
        SapCallContextMiddleware.Clean(new string('a', 101)).Should().BeNull();

    // ------------------------------------------------------------------ DB

    [Fact]
    public void Store_columns_match_table_type_in_sql_script()
    {
        var ddl = SqlScripts.Batches("SapCallLog.sql").Single(b => b.Contains("CREATE TYPE rdm.SapCallLogTableType", StringComparison.Ordinal));
        var columns = Regex.Matches(ddl, @"^\s+(\w+)\s+(nvarchar|varchar|uniqueidentifier|datetime2|int|bit)", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();

        columns.Should().Equal(SapCallLogStore.Columns);
        using var table = SapCallLogStore.ToDataTable(new[] { Entry() });
        table.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName).Should().Equal(SapCallLogStore.Columns);
    }

    [Fact]
    public void Long_values_are_cut_to_column_sizes()
    {
        using var table = SapCallLogStore.ToDataTable(new[]
        {
            Entry() with { Message = new string('x', 5000), Context = new SapCallContext { Path = new string('p', 900) } }
        });

        ((string)table.Rows[0]["Message"]).Length.Should().Be(1000);
        ((string)table.Rows[0]["Path"]).Length.Should().Be(300);
    }

    // ------------------------------------------------------------------ bộ ghi nền

    private sealed class FakeStore : ISapCallLogStore
    {
        public readonly List<IReadOnlyList<SapCallLogEntry>> Batches = new();
        public int FailNext;

        public Task InsertBatchAsync(IReadOnlyList<SapCallLogEntry> entries, CancellationToken ct)
        {
            if (FailNext > 0) { FailNext--; throw new InvalidOperationException("DB không kết nối được"); }
            lock (Batches) Batches.Add(entries.ToList());
            return Task.CompletedTask;
        }

        public int Count { get { lock (Batches) return Batches.Sum(b => b.Count); } }
        public IEnumerable<Guid> Ids { get { lock (Batches) return Batches.SelectMany(b => b).Select(e => e.CallId).ToList(); } }

        public Task<SapCallLogPage> SearchAsync(SapCallLogFilter filter, CancellationToken ct) => throw new NotSupportedException();
        public Task<SapCallLogDetail?> GetAsync(long id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SapCallLogSummaryRow>> SummaryAsync(DateTime f, DateTime t, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SapCallReconcileRow>> ReconcileAsync(DateTime f, DateTime t, string? u, string? r, string? c, int m, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<int> PurgeAsync(DateTime beforeUtc, CancellationToken ct) => throw new NotSupportedException();
    }

    private static SapCallLogEntry Entry() => new()
    {
        StartedAtUtc = DateTime.UtcNow, FunctionName = "Z_TEST", RequestJson = "{\"A\":1}", ResponseJson = "{}",
        Context = new SapCallContext { CorrelationId = "EU-1", RequestId = "REQ-1" }
    };

    private static (SapCallLogWriter Writer, string Dir) NewWriter(FakeStore store, int batchSize = 50, int capacity = 10_000)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rdm-sapcall-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SapCallLog:BatchSize"] = batchSize.ToString(),
            ["SapCallLog:FlushIntervalMs"] = "50",
            ["SapCallLog:QueueCapacity"] = capacity.ToString(),
            ["SapCallLog:FallbackDirectory"] = dir
        }).Build();
        return (new SapCallLogWriter(store, config, NullLogger<SapCallLogWriter>.Instance), dir);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
    }

    [Fact]
    public async Task Writer_batches_entries_and_attaches_as_global_sink()
    {
        var store = new FakeStore();
        var (writer, _) = NewWriter(store, batchSize: 50);
        await writer.StartAsync(CancellationToken.None);
        try
        {
            SapCallLog.Sink.Should().BeSameAs(writer);
            for (var i = 0; i < 230; i++) writer.Write(Entry());

            await WaitUntil(() => store.Count == 230);

            store.Count.Should().Be(230);
            store.Batches.Should().OnlyContain(b => b.Count <= 50);
            store.Batches.Count.Should().BeLessThan(20, "phải gom lô, không ghi từng dòng");
            writer.Stats().Written.Should().Be(230);
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }
        SapCallLog.Sink.Should().NotBeSameAs(writer);
    }

    [Fact]
    public async Task Db_failure_goes_to_fallback_file_and_is_replayed_without_loss()
    {
        var store = new FakeStore { FailNext = 1 };
        var (writer, dir) = NewWriter(store, batchSize: 100);
        await writer.StartAsync(CancellationToken.None);
        try
        {
            var first = Enumerable.Range(0, 10).Select(_ => Entry()).ToList();
            first.ForEach(writer.Write);
            await WaitUntil(() => writer.Stats().FallbackWritten == 10);
            Directory.GetFiles(dir, "sapcall-*.jsonl").Should().HaveCount(1);

            writer.Write(Entry()); // DB chạy lại → ghi bản mới + nạp lại file
            await WaitUntil(() => store.Count == 11 && Directory.GetFiles(dir).Length == 0);

            store.Ids.Should().Contain(first.Select(e => e.CallId));
            store.Count.Should().Be(11);
            Directory.GetFiles(dir).Should().BeEmpty();
            writer.Stats().FailedBatches.Should().Be(1);
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Stop_flushes_remaining_queue()
    {
        var store = new FakeStore();
        var (writer, _) = NewWriter(store, batchSize: 1000);
        await writer.StartAsync(CancellationToken.None);
        for (var i = 0; i < 500; i++) writer.Write(Entry());

        await writer.StopAsync(CancellationToken.None);

        store.Count.Should().Be(500);
    }

    [Fact]
    public void Full_queue_drops_instead_of_blocking_the_sap_call()
    {
        var store = new FakeStore();
        var (writer, _) = NewWriter(store, capacity: 100); // chưa Start → không ai đọc hàng đợi

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) writer.Write(Entry());
        sw.Stop();

        writer.Stats().Enqueued.Should().Be(100);
        writer.Stats().Dropped.Should().Be(900);
        sw.ElapsedMilliseconds.Should().BeLessThan(500);
    }
}
