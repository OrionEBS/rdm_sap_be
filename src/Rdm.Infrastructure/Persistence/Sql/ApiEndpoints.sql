-- Bảng cấu hình active / inactive API (rdm.ApiEndpoints) — ghi chỉ qua procedure.

CREATE TYPE rdm.ApiEndpointTableType AS TABLE (
    EndpointKey   nvarchar(300) NOT NULL PRIMARY KEY,
    HttpMethod    nvarchar(10)  NOT NULL,
    RouteTemplate nvarchar(256) NOT NULL,
    GroupName     nvarchar(100) NOT NULL,
    ActionName    nvarchar(100) NOT NULL,
    IsLocked      bit           NOT NULL
);
GO

CREATE TYPE rdm.StringListTableType AS TABLE (
    Value nvarchar(300) NOT NULL PRIMARY KEY
);
GO

-- Đồng bộ danh sách API trong code lúc khởi động: thêm mới (IsActive = @DefaultActive), cập nhật thông tin + LastSeenAtUtc.
-- KHÔNG xóa API không còn trong code (giữ lịch sử, nhận biết qua LastSeenAtUtc cũ). API khóa luôn active.
CREATE PROCEDURE rdm.usp_ApiEndpoint_Sync
    @Endpoints rdm.ApiEndpointTableType READONLY, @DefaultActive bit, @NowUtc datetime2
AS
BEGIN
    SET NOCOUNT ON;
    MERGE rdm.ApiEndpoints AS t
    USING @Endpoints AS s ON t.EndpointKey = s.EndpointKey
    WHEN MATCHED THEN UPDATE SET
        HttpMethod = s.HttpMethod, RouteTemplate = s.RouteTemplate, GroupName = s.GroupName, ActionName = s.ActionName,
        IsLocked = s.IsLocked, IsActive = CASE WHEN s.IsLocked = 1 THEN 1 ELSE t.IsActive END, LastSeenAtUtc = @NowUtc
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (EndpointKey, HttpMethod, RouteTemplate, GroupName, ActionName, IsActive, IsLocked, CreatedAtUtc, LastSeenAtUtc)
        VALUES (s.EndpointKey, s.HttpMethod, s.RouteTemplate, s.GroupName, s.ActionName,
                CASE WHEN s.IsLocked = 1 THEN 1 ELSE @DefaultActive END, s.IsLocked, @NowUtc, @NowUtc);
END
GO

-- Bật / tắt theo khóa và / hoặc nhóm (GroupName). Bỏ qua API khóa và API đã ở đúng trạng thái. Trả các khóa đã đổi.
CREATE PROCEDURE rdm.usp_ApiEndpoint_SetActive
    @Keys rdm.StringListTableType READONLY, @Groups rdm.StringListTableType READONLY,
    @IsActive bit, @UpdatedBy nvarchar(100), @Note nvarchar(500), @NowUtc datetime2
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE t
       SET IsActive = @IsActive, UpdatedBy = @UpdatedBy, UpdatedAtUtc = @NowUtc, Note = ISNULL(@Note, t.Note)
    OUTPUT inserted.EndpointKey
      FROM rdm.ApiEndpoints t
     WHERE t.IsLocked = 0 AND t.IsActive <> @IsActive
       AND (t.EndpointKey IN (SELECT Value FROM @Keys) OR t.GroupName IN (SELECT Value FROM @Groups));
END
GO
