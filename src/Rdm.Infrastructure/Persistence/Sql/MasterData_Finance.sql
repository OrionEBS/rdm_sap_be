-- Master data tài chính (mục 11): tài khoản GL, cost center.
-- Thứ tự cột của TVP phải khớp MasterDataRepository.Targets.

CREATE TYPE md.GlAccountTableType AS TABLE (
    CompanyCode     nvarchar(4)  NOT NULL,
    AccountNumber   nvarchar(10) NOT NULL,
    AccountCode     nvarchar(10) NOT NULL,
    ChartOfAccounts nvarchar(4)  NOT NULL,
    ShortText       nvarchar(20) NULL,
    LongText        nvarchar(50) NULL,
    SyncedAtUtc     datetime2    NOT NULL,
    PRIMARY KEY (CompanyCode, AccountNumber));
GO
CREATE PROCEDURE md.usp_GlAccount_Replace @Rows md.GlAccountTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.GlAccount;
        INSERT INTO md.GlAccount (CompanyCode, AccountNumber, AccountCode, ChartOfAccounts, ShortText, LongText, SyncedAtUtc)
        SELECT CompanyCode, AccountNumber, AccountCode, ChartOfAccounts, ShortText, LongText, SyncedAtUtc FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
GO

CREATE TYPE md.CostCenterTableType AS TABLE (
    ControllingArea  nvarchar(4)  NOT NULL,
    Code             nvarchar(10) NOT NULL,
    Name             nvarchar(20) NULL,
    Description      nvarchar(40) NULL,
    ActivationStatus nvarchar(1)  NULL,
    SyncedAtUtc      datetime2    NOT NULL,
    PRIMARY KEY (ControllingArea, Code));
GO
CREATE PROCEDURE md.usp_CostCenter_Replace @Rows md.CostCenterTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.CostCenter;
        INSERT INTO md.CostCenter (ControllingArea, Code, Name, Description, ActivationStatus, SyncedAtUtc)
        SELECT ControllingArea, Code, Name, Description, ActivationStatus, SyncedAtUtc FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
