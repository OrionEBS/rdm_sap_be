-- Nhật ký gọi SAP (bằng chứng đối soát với EU): mỗi lời gọi function module 1 dòng — tham số gửi / kết quả nhận (JSON nén
-- COMPRESS = GZIP), thời gian, trạng thái, ngữ cảnh (X-Correlation-Id, requestId, user, IP, URL, job).
-- Ghi CHỈ qua rdm.usp_SapCallLog_InsertBatch (lô từ bộ ghi nền). Khóa cụm = Id tăng dần → chèn cuối bảng, không phân mảnh.

CREATE TABLE rdm.SapCallLog (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SapCallLog PRIMARY KEY CLUSTERED,
    CallId uniqueidentifier NOT NULL,
    StartedAtUtc datetime2(3) NOT NULL,
    DurationMs int NOT NULL,
    FunctionName varchar(64) NOT NULL,
    Status varchar(12) NOT NULL,
    ErrorCode varchar(100) NULL,
    Message nvarchar(1000) NULL,
    CorrelationId varchar(100) NULL,
    RequestId nvarchar(64) NULL,
    Operation varchar(64) NULL,
    PostingId uniqueidentifier NULL,
    Source varchar(16) NULL,
    UserName nvarchar(100) NULL,
    ClientIp varchar(64) NULL,
    HttpMethod varchar(10) NULL,
    Path nvarchar(300) NULL,
    TraceId varchar(64) NULL,
    JobId varchar(64) NULL,
    Machine nvarchar(64) NULL,
    SapSystem varchar(32) NULL,
    RequestBytes int NOT NULL,
    ResponseBytes int NOT NULL,
    Truncated bit NOT NULL,
    RequestJsonZ varbinary(max) NULL,
    ResponseJsonZ varbinary(max) NULL,
    LoggedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_SapCallLog_LoggedAtUtc DEFAULT (SYSUTCDATETIME())
);
GO

CREATE INDEX IX_SapCallLog_StartedAtUtc ON rdm.SapCallLog (StartedAtUtc) INCLUDE (FunctionName, Status, DurationMs);
GO
CREATE INDEX IX_SapCallLog_CorrelationId ON rdm.SapCallLog (CorrelationId, StartedAtUtc) WHERE CorrelationId IS NOT NULL;
GO
CREATE INDEX IX_SapCallLog_RequestId ON rdm.SapCallLog (RequestId, StartedAtUtc) WHERE RequestId IS NOT NULL;
GO
CREATE INDEX IX_SapCallLog_PostingId ON rdm.SapCallLog (PostingId) WHERE PostingId IS NOT NULL;
GO
CREATE UNIQUE INDEX UX_SapCallLog_CallId ON rdm.SapCallLog (CallId);
GO

CREATE TYPE rdm.SapCallLogTableType AS TABLE (
    CallId uniqueidentifier NOT NULL,
    StartedAtUtc datetime2(3) NOT NULL,
    DurationMs int NOT NULL,
    FunctionName varchar(64) NOT NULL,
    Status varchar(12) NOT NULL,
    ErrorCode varchar(100) NULL,
    Message nvarchar(1000) NULL,
    CorrelationId varchar(100) NULL,
    RequestId nvarchar(64) NULL,
    Operation varchar(64) NULL,
    PostingId uniqueidentifier NULL,
    Source varchar(16) NULL,
    UserName nvarchar(100) NULL,
    ClientIp varchar(64) NULL,
    HttpMethod varchar(10) NULL,
    Path nvarchar(300) NULL,
    TraceId varchar(64) NULL,
    JobId varchar(64) NULL,
    Machine nvarchar(64) NULL,
    SapSystem varchar(32) NULL,
    Truncated bit NOT NULL,
    RequestJson nvarchar(max) NULL,
    ResponseJson nvarchar(max) NULL
);
GO

-- Chèn 1 lô. Bỏ qua CallId đã có (bộ ghi gửi lại lô sau lỗi mạng / nạp lại file dự phòng) → không trùng.
CREATE OR ALTER PROCEDURE rdm.usp_SapCallLog_InsertBatch @Rows rdm.SapCallLogTableType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO rdm.SapCallLog (CallId, StartedAtUtc, DurationMs, FunctionName, Status, ErrorCode, Message, CorrelationId, RequestId,
                                Operation, PostingId, Source, UserName, ClientIp, HttpMethod, Path, TraceId, JobId, Machine, SapSystem,
                                RequestBytes, ResponseBytes, Truncated, RequestJsonZ, ResponseJsonZ)
    SELECT r.CallId, r.StartedAtUtc, r.DurationMs, r.FunctionName, r.Status, r.ErrorCode, r.Message, r.CorrelationId, r.RequestId,
           r.Operation, r.PostingId, r.Source, r.UserName, r.ClientIp, r.HttpMethod, r.Path, r.TraceId, r.JobId, r.Machine, r.SapSystem,
           ISNULL(DATALENGTH(r.RequestJson), 0) / 2, ISNULL(DATALENGTH(r.ResponseJson), 0) / 2, r.Truncated,
           COMPRESS(r.RequestJson), COMPRESS(r.ResponseJson)
      FROM @Rows r
     WHERE NOT EXISTS (SELECT 1 FROM rdm.SapCallLog l WHERE l.CallId = r.CallId);
    SELECT @@ROWCOUNT AS Inserted;
END
GO

-- Xóa nhật ký cũ theo lô nhỏ (không khóa bảng lâu, không phình log giao dịch). Trả số dòng đã xóa.
CREATE OR ALTER PROCEDURE rdm.usp_SapCallLog_Purge @BeforeUtc datetime2(3), @BatchSize int = 5000
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @total int = 0, @n int = 1;
    WHILE @n > 0
    BEGIN
        DELETE TOP (@BatchSize) FROM rdm.SapCallLog WHERE StartedAtUtc < @BeforeUtc;
        SET @n = @@ROWCOUNT;
        SET @total += @n;
    END
    SELECT @total AS Deleted;
END
GO

-- Xem nhanh trên SSMS (JSON đã giải nén).
CREATE OR ALTER VIEW rdm.vw_SapCallLog
AS
SELECT Id, CallId, StartedAtUtc, DurationMs, FunctionName, Status, ErrorCode, Message, CorrelationId, RequestId, Operation, PostingId,
       Source, UserName, ClientIp, HttpMethod, Path, TraceId, JobId, Machine, SapSystem, RequestBytes, ResponseBytes, Truncated,
       CAST(DECOMPRESS(RequestJsonZ) AS nvarchar(max)) AS RequestJson,
       CAST(DECOMPRESS(ResponseJsonZ) AS nvarchar(max)) AS ResponseJson
  FROM rdm.SapCallLog;
GO
