namespace Rdm.Application.Postings;

/// <summary>Tiến độ 1 thao tác hàng loạt: tổng số record, đã xử lý, thành công, lỗi.</summary>
public readonly record struct ProgressSnapshot(int Total, int Done, int Succeeded, int Failed);

/// <summary>
/// Tiến độ của job xử lý nền, truyền ngầm (AsyncLocal) từ job xuống tầng SAP — không đổi chữ ký ISapClient.
/// Tầng SAP cập nhật từ thread NCo; job đọc định kỳ và lưu vào rdm.SapPostings. Thread-safe.
/// </summary>
public sealed class BulkProgress
{
    private static readonly AsyncLocal<BulkProgress?> CurrentSlot = new();

    private int _total, _done, _succeeded, _failed;

    /// <summary>Tiến độ của job đang chạy trong luồng gọi hiện tại; null khi gọi đồng bộ (không cần báo tiến độ).</summary>
    public static BulkProgress? Current => CurrentSlot.Value;

    public static IDisposable Begin(BulkProgress progress)
    {
        var previous = CurrentSlot.Value;
        CurrentSlot.Value = progress;
        return new Scope(previous);
    }

    public void SetTotal(int total) => Volatile.Write(ref _total, total);

    public void Add(int succeeded, int failed)
    {
        Interlocked.Add(ref _succeeded, succeeded);
        Interlocked.Add(ref _failed, failed);
        Interlocked.Add(ref _done, succeeded + failed);
    }

    /// <summary>Record bỏ qua (đã ở trạng thái đích): tính vào đã xử lý, không vào thành công / lỗi.</summary>
    public void Skip(int count) => Interlocked.Add(ref _done, count);

    public ProgressSnapshot Snapshot() =>
        new(Volatile.Read(ref _total), Volatile.Read(ref _done), Volatile.Read(ref _succeeded), Volatile.Read(ref _failed));

    private sealed class Scope : IDisposable
    {
        private readonly BulkProgress? _previous;
        public Scope(BulkProgress? previous) => _previous = previous;
        public void Dispose() => CurrentSlot.Value = _previous;
    }
}
