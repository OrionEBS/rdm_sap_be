-- Cấu hình cột output theo API (ẩn / hiện, đổi tên, thứ tự) + API sao chép (CopyName) — chỉ ghi qua procedure.

-- Thêm / sửa 1 cấu hình. @ProfileId NULL: thêm mới; riêng cấu hình gốc (@CopyName NULL) đã có cho @EndpointKey thì sửa dòng đó.
-- Trùng CopyName → lỗi 50409. Trả ProfileId.
CREATE OR ALTER PROCEDURE rdm.usp_ApiOutputProfile_Save
    @ProfileId int, @EndpointKey nvarchar(300), @CopyName nvarchar(60), @Title nvarchar(200), @IsActive bit, @FieldsJson nvarchar(max),
    @UpdatedBy nvarchar(100), @NowUtc datetime2
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    SET @CopyName = NULLIF(LTRIM(RTRIM(@CopyName)), N'');
    SET @Title = NULLIF(LTRIM(RTRIM(@Title)), N'');
    BEGIN TRAN;
        IF @ProfileId IS NULL AND @CopyName IS NULL
            SELECT @ProfileId = ProfileId FROM rdm.ApiOutputProfiles WITH (UPDLOCK, HOLDLOCK)
             WHERE EndpointKey = @EndpointKey AND CopyName IS NULL;

        IF @CopyName IS NOT NULL AND EXISTS (SELECT 1 FROM rdm.ApiOutputProfiles WITH (UPDLOCK, HOLDLOCK)
                                             WHERE CopyName = @CopyName AND (@ProfileId IS NULL OR ProfileId <> @ProfileId))
        BEGIN
            ROLLBACK;
            THROW 50409, N'Tên API sao chép đã tồn tại.', 1;
        END

        IF @ProfileId IS NULL
        BEGIN
            INSERT INTO rdm.ApiOutputProfiles (EndpointKey, CopyName, Title, IsActive, FieldsJson, UpdatedBy, UpdatedAtUtc, CreatedAtUtc)
            VALUES (@EndpointKey, @CopyName, @Title, @IsActive, @FieldsJson, @UpdatedBy, @NowUtc, @NowUtc);
            SET @ProfileId = CAST(SCOPE_IDENTITY() AS int);
        END
        ELSE
        BEGIN
            UPDATE rdm.ApiOutputProfiles
               SET CopyName = CASE WHEN CopyName IS NULL THEN NULL ELSE @CopyName END,   -- cấu hình gốc không đổi thành bản sao
                   Title = @Title, IsActive = @IsActive, FieldsJson = @FieldsJson, UpdatedBy = @UpdatedBy, UpdatedAtUtc = @NowUtc
             WHERE ProfileId = @ProfileId;
            IF @@ROWCOUNT = 0 SET @ProfileId = NULL;
        END
    COMMIT;
    SELECT @ProfileId AS ProfileId;
END
GO

-- Xóa cấu hình: cấu hình gốc → API trả output như ban đầu; bản sao → API sao chép bị xóa. Trả 1 nếu có xóa.
CREATE OR ALTER PROCEDURE rdm.usp_ApiOutputProfile_Delete
    @ProfileId int
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM rdm.ApiOutputProfiles WHERE ProfileId = @ProfileId;
    SELECT CAST(CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END AS bit) AS Deleted;
END
GO

CREATE TYPE rdm.ApiOutputProfileImportTableType AS TABLE (
    EndpointKey nvarchar(300) NOT NULL,
    CopyName nvarchar(60) NULL,
    Title nvarchar(200) NULL,
    IsActive bit NOT NULL,
    FieldsJson nvarchar(max) NOT NULL,
    UpdatedBy nvarchar(100) NULL,
    UpdatedAtUtc datetime2 NULL,
    CreatedAtUtc datetime2 NOT NULL
);
GO

-- Nhập cấu hình output từ DB khác (Rdm.DbSync). Khóa: CopyName (bản sao) hoặc EndpointKey (cấu hình gốc).
-- @OnlyUntouched = 1: chỉ thêm cấu hình chưa có ở DB đích, không đè cấu hình bên đó. Không xóa dòng thừa ở đích.
CREATE OR ALTER PROCEDURE rdm.usp_ApiOutputProfile_Import
    @Rows rdm.ApiOutputProfileImportTableType READONLY, @OnlyUntouched bit
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @inserted int, @updated int = 0;
    BEGIN TRAN;
        IF @OnlyUntouched = 0
        BEGIN
            UPDATE t
               SET EndpointKey = r.EndpointKey, Title = r.Title, IsActive = r.IsActive, FieldsJson = r.FieldsJson,
                   UpdatedBy = r.UpdatedBy, UpdatedAtUtc = r.UpdatedAtUtc
              FROM rdm.ApiOutputProfiles t WITH (UPDLOCK, HOLDLOCK)
              JOIN @Rows r ON (r.CopyName IS NOT NULL AND t.CopyName = r.CopyName)
                           OR (r.CopyName IS NULL AND t.CopyName IS NULL AND t.EndpointKey = r.EndpointKey);
            SET @updated = @@ROWCOUNT;
        END

        INSERT INTO rdm.ApiOutputProfiles (EndpointKey, CopyName, Title, IsActive, FieldsJson, UpdatedBy, UpdatedAtUtc, CreatedAtUtc)
        SELECT r.EndpointKey, r.CopyName, r.Title, r.IsActive, r.FieldsJson, r.UpdatedBy, r.UpdatedAtUtc, r.CreatedAtUtc
          FROM @Rows r
         WHERE NOT EXISTS (SELECT 1 FROM rdm.ApiOutputProfiles t
                            WHERE (r.CopyName IS NOT NULL AND t.CopyName = r.CopyName)
                               OR (r.CopyName IS NULL AND t.CopyName IS NULL AND t.EndpointKey = r.EndpointKey));
        SET @inserted = @@ROWCOUNT;
    COMMIT;
    SELECT @inserted AS Inserted, @updated AS Updated, (SELECT COUNT(*) FROM @Rows) - @inserted - @updated AS Skipped;
END
GO

CREATE OR ALTER VIEW rdm.vw_ApiOutputProfiles
AS
SELECT p.ProfileId, p.EndpointKey, p.CopyName,
       CASE WHEN p.CopyName IS NULL THEN N'Cấu hình API gốc' ELSE N'API sao chép' END AS [Loai],
       p.Title, p.IsActive, e.IsActive AS SourceIsActive, p.FieldsJson, p.UpdatedBy, p.UpdatedAtUtc, p.CreatedAtUtc
  FROM rdm.ApiOutputProfiles p
  LEFT JOIN rdm.ApiEndpoints e ON e.EndpointKey = p.EndpointKey;
GO
