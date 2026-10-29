-- Bảng cấu hình API: mô tả API (Description), view xem nhanh, procedure bật / tắt theo mẫu.
-- Quản trị qua trang /admin (API api/admin/endpoints → procedure); không dùng trigger.
-- Ứng dụng tự nạp lại sau tối đa ApiEndpoints:RefreshSeconds (30 giây) hoặc ngay khi gọi POST /api/admin/endpoints/refresh.

-- TVP thêm Description → xóa procedure phụ thuộc trước khi tạo lại kiểu
DROP PROCEDURE IF EXISTS rdm.usp_ApiEndpoint_Sync;
GO
DROP TYPE IF EXISTS rdm.ApiEndpointTableType;
GO
CREATE TYPE rdm.ApiEndpointTableType AS TABLE (
    EndpointKey   nvarchar(300) NOT NULL PRIMARY KEY,
    HttpMethod    nvarchar(10)  NOT NULL,
    RouteTemplate nvarchar(256) NOT NULL,
    GroupName     nvarchar(100) NOT NULL,
    ActionName    nvarchar(100) NOT NULL,
    IsLocked      bit           NOT NULL,
    Description   nvarchar(500) NULL
);
GO

CREATE PROCEDURE rdm.usp_ApiEndpoint_Sync
    @Endpoints rdm.ApiEndpointTableType READONLY, @DefaultActive bit, @NowUtc datetime2
AS
BEGIN
    SET NOCOUNT ON;
    MERGE rdm.ApiEndpoints AS t
    USING @Endpoints AS s ON t.EndpointKey = s.EndpointKey
    WHEN MATCHED THEN UPDATE SET
        HttpMethod = s.HttpMethod, RouteTemplate = s.RouteTemplate, GroupName = s.GroupName, ActionName = s.ActionName,
        Description = s.Description, IsLocked = s.IsLocked,
        IsActive = CASE WHEN s.IsLocked = 1 THEN 1 ELSE t.IsActive END, LastSeenAtUtc = @NowUtc
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (EndpointKey, HttpMethod, RouteTemplate, GroupName, ActionName, Description, IsActive, IsLocked, CreatedAtUtc, LastSeenAtUtc)
        VALUES (s.EndpointKey, s.HttpMethod, s.RouteTemplate, s.GroupName, s.ActionName, s.Description,
                CASE WHEN s.IsLocked = 1 THEN 1 ELSE @DefaultActive END, s.IsLocked, @NowUtc, @NowUtc);
END
GO

CREATE OR ALTER PROCEDURE rdm.usp_ApiEndpoint_SetActive
    @Keys rdm.StringListTableType READONLY, @Groups rdm.StringListTableType READONLY,
    @IsActive bit, @UpdatedBy nvarchar(100), @Note nvarchar(500), @NowUtc datetime2
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @changed TABLE (EndpointKey nvarchar(300) NOT NULL);
    UPDATE t
       SET IsActive = @IsActive, UpdatedBy = @UpdatedBy, UpdatedAtUtc = @NowUtc, Note = ISNULL(@Note, t.Note)
    OUTPUT inserted.EndpointKey INTO @changed (EndpointKey)
      FROM rdm.ApiEndpoints t
     WHERE t.IsLocked = 0 AND t.IsActive <> @IsActive
       AND (t.EndpointKey IN (SELECT Value FROM @Keys) OR t.GroupName IN (SELECT Value FROM @Groups));
    SELECT EndpointKey FROM @changed;
END
GO

-- Bật / tắt nhanh theo mẫu LIKE trên khóa, route hoặc nhóm (DBA). VD:
--   EXEC rdm.usp_ApiEndpoint_SetActiveByPattern N'%prices-bapi%', 0, N'chưa mở cho team test';
--   EXEC rdm.usp_ApiEndpoint_SetActiveByPattern N'MasterData', 1;
CREATE OR ALTER PROCEDURE rdm.usp_ApiEndpoint_SetActiveByPattern
    @Pattern nvarchar(300), @IsActive bit, @Note nvarchar(500) = NULL, @UpdatedBy nvarchar(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @Pattern IS NULL OR LTRIM(RTRIM(@Pattern)) IN (N'', N'%')
        THROW 50110, N'@Pattern bắt buộc và không được là ''%'' (tắt toàn bộ) — VD N''%prices-bapi%'', N''MasterData''.', 1;
    DECLARE @changed TABLE (GroupName nvarchar(100), HttpMethod nvarchar(10), RouteTemplate nvarchar(256), IsActive bit);
    UPDATE t
       SET IsActive = @IsActive, Note = ISNULL(@Note, t.Note), UpdatedAtUtc = SYSUTCDATETIME(),
           UpdatedBy = ISNULL(@UpdatedBy, LEFT(ORIGINAL_LOGIN() + N' (SQL)', 100))
    OUTPUT inserted.GroupName, inserted.HttpMethod, inserted.RouteTemplate, inserted.IsActive INTO @changed
      FROM rdm.ApiEndpoints t
     WHERE t.IsLocked = 0 AND t.IsActive <> @IsActive
       AND (t.EndpointKey LIKE @Pattern OR t.RouteTemplate LIKE @Pattern OR t.GroupName LIKE @Pattern);
    SELECT GroupName AS [Nhom], HttpMethod AS [Method], N'/' + RouteTemplate AS [Route], IsActive FROM @changed ORDER BY GroupName, RouteTemplate;
END
GO

-- Xem nhanh (chỉ đọc): nhóm, method, route, mô tả, trạng thái, còn trong code hay không.
CREATE OR ALTER VIEW rdm.vw_ApiEndpoints
AS
SELECT GroupName AS [Nhom], HttpMethod AS [Method], N'/' + RouteTemplate AS [Route], Description AS [MoTa],
       IsActive, IsLocked, Note, UpdatedBy, UpdatedAtUtc,
       CAST(CASE WHEN LastSeenAtUtc >= DATEADD(MINUTE, -1, MAX(LastSeenAtUtc) OVER ()) THEN 1 ELSE 0 END AS bit) AS [InCode],
       EndpointKey
  FROM rdm.ApiEndpoints;
GO
