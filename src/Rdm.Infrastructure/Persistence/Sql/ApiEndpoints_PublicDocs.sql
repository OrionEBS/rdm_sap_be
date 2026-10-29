-- Mô tả API cho ĐỐI TÁC (Swagger công khai) — tách khỏi mô tả nội bộ (Description, kỹ thuật SAP, tự lấy từ code).

-- Lưu mô tả đối tác của 1 API (chuỗi rỗng = xóa). Trả 1 nếu có khóa.
CREATE OR ALTER PROCEDURE rdm.usp_ApiEndpoint_SetDoc
    @EndpointKey nvarchar(300), @PublicSummary nvarchar(200), @PublicDescription nvarchar(2000), @PublicHidden bit,
    @UpdatedBy nvarchar(100), @NowUtc datetime2
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE rdm.ApiEndpoints
       SET PublicSummary = NULLIF(LTRIM(RTRIM(@PublicSummary)), N''),
           PublicDescription = NULLIF(LTRIM(RTRIM(@PublicDescription)), N''), PublicHidden = @PublicHidden,
           DocUpdatedBy = @UpdatedBy, DocUpdatedAtUtc = @NowUtc
     WHERE EndpointKey = @EndpointKey;
    SELECT CAST(CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END AS bit) AS Updated;
END
GO

CREATE OR ALTER VIEW rdm.vw_ApiEndpoints
AS
SELECT GroupName AS [Nhom], HttpMethod AS [Method], N'/' + RouteTemplate AS [Route],
       PublicSummary AS [MoTaDoiTac], Description AS [MoTaNoiBo], PublicHidden AS [AnTaiLieuDoiTac],
       IsActive, IsLocked, Note, UpdatedBy, UpdatedAtUtc, DocUpdatedBy, DocUpdatedAtUtc,
       CAST(CASE WHEN LastSeenAtUtc >= DATEADD(MINUTE, -1, MAX(LastSeenAtUtc) OVER ()) THEN 1 ELSE 0 END AS bit) AS [InCode],
       EndpointKey
  FROM rdm.ApiEndpoints;
GO
