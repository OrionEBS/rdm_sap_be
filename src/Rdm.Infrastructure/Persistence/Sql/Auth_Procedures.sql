-- Tài khoản gọi API: ghi qua procedure.

-- @PasswordHash = NULL → giữ mật khẩu cũ (chỉ hợp lệ khi tài khoản đã tồn tại).
CREATE PROCEDURE rdm.usp_ApiUser_Upsert
    @Username nvarchar(64), @PasswordHash nvarchar(500), @Role nvarchar(32), @IsActive bit, @NowUtc datetime2
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRAN;
        UPDATE rdm.ApiUsers WITH (UPDLOCK, HOLDLOCK)
           SET PasswordHash = COALESCE(@PasswordHash, PasswordHash), Role = @Role, IsActive = @IsActive, UpdatedAtUtc = @NowUtc
         WHERE Username = @Username;
        IF @@ROWCOUNT = 0
        BEGIN
            IF @PasswordHash IS NULL THROW 50010, N'Tạo tài khoản mới phải có mật khẩu.', 1;
            INSERT INTO rdm.ApiUsers (Username, PasswordHash, Role, IsActive, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@Username, @PasswordHash, @Role, @IsActive, @NowUtc, @NowUtc);
        END
    COMMIT;
END
GO

CREATE PROCEDURE rdm.usp_ApiUser_SetLastLogin @Username nvarchar(64), @NowUtc datetime2
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE rdm.ApiUsers SET LastLoginAtUtc = @NowUtc WHERE Username = @Username;
END
