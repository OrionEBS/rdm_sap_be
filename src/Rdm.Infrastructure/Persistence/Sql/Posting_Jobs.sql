-- Xử lý nền cho posting (bảng giá hàng loạt). Ghi rdm.SapPostings chỉ qua procedure.

-- Giữ chỗ requestId (thêm @IsBackgroundJob; gọi cũ không truyền → 0).
CREATE OR ALTER PROCEDURE rdm.usp_SapPosting_TryInsert
    @Id uniqueidentifier, @Operation nvarchar(32), @RequestId nvarchar(64), @RequestHash varchar(64),
    @RequestJson nvarchar(max), @Status nvarchar(16), @Attempts int, @CreatedAtUtc datetime2, @StartedAtUtc datetime2,
    @IsBackgroundJob bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        INSERT INTO rdm.SapPostings (Id, Operation, RequestId, RequestHash, RequestJson, Status, Attempts, CreatedAtUtc, StartedAtUtc,
                                     IsBackgroundJob)
        VALUES (@Id, @Operation, @RequestId, @RequestHash, @RequestJson, @Status, @Attempts, @CreatedAtUtc, @StartedAtUtc,
                @IsBackgroundJob);
        SELECT CAST(1 AS bit) AS Inserted;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() IN (2601, 2627) SELECT CAST(0 AS bit) AS Inserted;
        ELSE THROW;
    END CATCH
END
GO

-- Failed → Pending nguyên tử; xóa dấu claim + tiến độ của lần chạy trước.
CREATE OR ALTER PROCEDURE rdm.usp_SapPosting_RestartFailed
    @Id uniqueidentifier, @RequestJson nvarchar(max), @RequestHash varchar(64), @NowUtc datetime2,
    @IsBackgroundJob bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE rdm.SapPostings
       SET Status = N'Pending', Attempts = Attempts + 1, RequestJson = @RequestJson, RequestHash = @RequestHash,
           StartedAtUtc = @NowUtc, CompletedAtUtc = NULL, ErrorCode = NULL, ErrorMessage = NULL, ReturnMessagesJson = NULL,
           IsBackgroundJob = @IsBackgroundJob, JobClaimedAtUtc = NULL,
           ProgressTotal = NULL, ProgressDone = NULL, ProgressSucceeded = NULL, ProgressFailed = NULL
     WHERE Id = @Id AND Status = N'Failed';
    SELECT CAST(CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END AS bit) AS Restarted;
END
GO

-- Job bắt đầu gọi SAP. Claimed = 0 nếu đã claim trước đó (lần chạy trước bị gián đoạn) hoặc không còn Pending.
CREATE OR ALTER PROCEDURE rdm.usp_SapPosting_ClaimJob
    @Id uniqueidentifier, @NowUtc datetime2
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE rdm.SapPostings
       SET JobClaimedAtUtc = @NowUtc
     WHERE Id = @Id AND Status = N'Pending' AND JobClaimedAtUtc IS NULL;
    SELECT CAST(CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END AS bit) AS Claimed;
END
GO

CREATE OR ALTER PROCEDURE rdm.usp_SapPosting_Progress
    @Id uniqueidentifier, @Total int, @Done int, @Succeeded int, @Failed int
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE rdm.SapPostings
       SET ProgressTotal = @Total, ProgressDone = @Done, ProgressSucceeded = @Succeeded, ProgressFailed = @Failed
     WHERE Id = @Id;
END
GO
