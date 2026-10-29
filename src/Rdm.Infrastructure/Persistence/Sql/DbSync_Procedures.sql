-- Đồng bộ DB (Rdm.DbSync): nhập cấu hình API (bật / tắt, ghi chú, mô tả đối tác) từ DB khác — ghi qua procedure, không ghi thẳng bảng.
-- Master data md.* dùng lại md.usp_*_Replace; tài khoản API dùng rdm.usp_ApiUser_Upsert.

CREATE TYPE rdm.ApiEndpointImportTableType AS TABLE (
    EndpointKey nvarchar(300) NOT NULL PRIMARY KEY,
    HttpMethod nvarchar(10) NOT NULL,
    RouteTemplate nvarchar(256) NOT NULL,
    GroupName nvarchar(100) NOT NULL,
    ActionName nvarchar(100) NOT NULL,
    Description nvarchar(500) NULL,
    PublicSummary nvarchar(200) NULL,
    PublicDescription nvarchar(2000) NULL,
    PublicHidden bit NOT NULL,
    DocUpdatedBy nvarchar(100) NULL,
    DocUpdatedAtUtc datetime2 NULL,
    IsActive bit NOT NULL,
    IsLocked bit NOT NULL,
    Note nvarchar(500) NULL,
    UpdatedBy nvarchar(100) NULL,
    UpdatedAtUtc datetime2 NULL,
    CreatedAtUtc datetime2 NOT NULL,
    LastSeenAtUtc datetime2 NOT NULL
);
GO

-- Nhập cấu hình API:
--   API chưa có ở DB đích → thêm nguyên dòng.
--   API đã có → chỉ chép phần do người quản trị chỉnh (IsActive, Note, mô tả đối tác, người / thời điểm sửa);
--   thông tin lấy từ code (route, nhóm, mô tả nội bộ, khóa, LastSeen) giữ của DB đích — API tự cập nhật khi khởi động.
--   API khóa ở DB đích luôn active.
--   @OnlyUntouched = 1: bỏ qua API đã được chỉnh trên DB đích (UpdatedAtUtc / DocUpdatedAtUtc có giá trị) — không đè cấu hình bên đó.
-- Không xóa API thừa ở DB đích. Trả Inserted, Updated, Skipped.
CREATE OR ALTER PROCEDURE rdm.usp_ApiEndpoint_Import
    @Rows rdm.ApiEndpointImportTableType READONLY, @OnlyUntouched bit
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @inserted int, @updated int;
    BEGIN TRAN;
        UPDATE t
           SET IsActive = CASE WHEN t.IsLocked = 1 THEN 1 ELSE r.IsActive END,
               Note = r.Note, UpdatedBy = r.UpdatedBy, UpdatedAtUtc = r.UpdatedAtUtc,
               PublicSummary = r.PublicSummary, PublicDescription = r.PublicDescription, PublicHidden = r.PublicHidden,
               DocUpdatedBy = r.DocUpdatedBy, DocUpdatedAtUtc = r.DocUpdatedAtUtc
          FROM rdm.ApiEndpoints t WITH (UPDLOCK, HOLDLOCK)
          JOIN @Rows r ON r.EndpointKey = t.EndpointKey
         WHERE @OnlyUntouched = 0 OR (t.UpdatedAtUtc IS NULL AND t.DocUpdatedAtUtc IS NULL);
        SET @updated = @@ROWCOUNT;

        INSERT INTO rdm.ApiEndpoints (EndpointKey, HttpMethod, RouteTemplate, GroupName, ActionName, Description, PublicSummary,
                                      PublicDescription, PublicHidden, DocUpdatedBy, DocUpdatedAtUtc, IsActive, IsLocked, Note,
                                      UpdatedBy, UpdatedAtUtc, CreatedAtUtc, LastSeenAtUtc)
        SELECT r.EndpointKey, r.HttpMethod, r.RouteTemplate, r.GroupName, r.ActionName, r.Description, r.PublicSummary,
               r.PublicDescription, r.PublicHidden, r.DocUpdatedBy, r.DocUpdatedAtUtc,
               CASE WHEN r.IsLocked = 1 THEN 1 ELSE r.IsActive END, r.IsLocked, r.Note,
               r.UpdatedBy, r.UpdatedAtUtc, r.CreatedAtUtc, r.LastSeenAtUtc
          FROM @Rows r
         WHERE NOT EXISTS (SELECT 1 FROM rdm.ApiEndpoints t WHERE t.EndpointKey = r.EndpointKey);
        SET @inserted = @@ROWCOUNT;
    COMMIT;
    SELECT @inserted AS Inserted, @updated AS Updated, (SELECT COUNT(*) FROM @Rows) - @inserted - @updated AS Skipped;
END
GO
