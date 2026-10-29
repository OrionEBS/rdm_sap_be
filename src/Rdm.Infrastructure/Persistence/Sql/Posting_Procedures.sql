-- Ghi rdm.SapPostings và dọn log qua procedure (không ghi trực tiếp bảng từ code).

-- Giữ chỗ requestId. Trả Inserted = 0 nếu (Operation, RequestId) đã tồn tại (thua race với request song song).
CREATE PROCEDURE rdm.usp_SapPosting_TryInsert
    @Id uniqueidentifier, @Operation nvarchar(32), @RequestId nvarchar(64), @RequestHash varchar(64),
    @RequestJson nvarchar(max), @Status nvarchar(16), @Attempts int, @CreatedAtUtc datetime2, @StartedAtUtc datetime2
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        INSERT INTO rdm.SapPostings (Id, Operation, RequestId, RequestHash, RequestJson, Status, Attempts, CreatedAtUtc, StartedAtUtc)
        VALUES (@Id, @Operation, @RequestId, @RequestHash, @RequestJson, @Status, @Attempts, @CreatedAtUtc, @StartedAtUtc);
        SELECT CAST(1 AS bit) AS Inserted;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() IN (2601, 2627) SELECT CAST(0 AS bit) AS Inserted;
        ELSE THROW;
    END CATCH
END
GO

-- Failed → Pending nguyên tử. Trả Restarted = 0 nếu bản ghi không còn ở trạng thái Failed.
CREATE PROCEDURE rdm.usp_SapPosting_RestartFailed
    @Id uniqueidentifier, @RequestJson nvarchar(max), @RequestHash varchar(64), @NowUtc datetime2
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE rdm.SapPostings
       SET Status = N'Pending', Attempts = Attempts + 1, RequestJson = @RequestJson, RequestHash = @RequestHash,
           StartedAtUtc = @NowUtc, CompletedAtUtc = NULL, ErrorCode = NULL, ErrorMessage = NULL, ReturnMessagesJson = NULL
     WHERE Id = @Id AND Status = N'Failed';
    SELECT CAST(CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END AS bit) AS Restarted;
END
GO

CREATE PROCEDURE rdm.usp_SapPosting_Complete
    @Id uniqueidentifier, @Status nvarchar(16),
    @DocumentNumber nvarchar(20), @CompanyCode nvarchar(4), @FiscalYear nvarchar(4),
    @ObjectType nvarchar(10), @ObjectKey nvarchar(20), @ObjectSystem nvarchar(10),
    @ErrorCode nvarchar(32), @ErrorMessage nvarchar(2000), @ReturnMessagesJson nvarchar(max),
    @CompletedAtUtc datetime2, @DurationMs int
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE rdm.SapPostings
       SET Status = @Status, DocumentNumber = @DocumentNumber, CompanyCode = @CompanyCode, FiscalYear = @FiscalYear,
           ObjectType = @ObjectType, ObjectKey = @ObjectKey, ObjectSystem = @ObjectSystem,
           ErrorCode = @ErrorCode, ErrorMessage = @ErrorMessage, ReturnMessagesJson = @ReturnMessagesJson,
           CompletedAtUtc = @CompletedAtUtc, DurationMs = @DurationMs
     WHERE Id = @Id;
    IF @@ROWCOUNT <> 1 THROW 50001, N'Không tìm thấy SapPosting để cập nhật kết quả.', 1;
END
GO

-- Bảng log do Serilog sink tự tạo lúc chạy → kiểm tra tồn tại, dùng SQL động.
CREATE PROCEDURE dbo.usp_SerilogEvents_Purge @Before datetime2
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @n int = 0;
    IF OBJECT_ID(N'dbo.SerilogEvents') IS NOT NULL
    BEGIN
        EXEC sp_executesql N'DELETE FROM dbo.SerilogEvents WHERE [TimeStamp] < @Before; SET @n = @@ROWCOUNT;',
             N'@Before datetime2, @n int OUTPUT', @Before = @Before, @n = @n OUTPUT;
    END
    SELECT @n AS Deleted;
END
