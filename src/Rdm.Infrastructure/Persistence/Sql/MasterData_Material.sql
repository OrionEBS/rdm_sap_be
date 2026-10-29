-- Master data vật tư (mục 10): Material, Material group, Material type, Division.
-- Thứ tự cột của TVP phải khớp MasterDataRepository.Targets.

CREATE TYPE md.MaterialTableType AS TABLE (
    MaterialNumber   nvarchar(18) NOT NULL PRIMARY KEY,
    MaterialCode     nvarchar(18) NOT NULL,
    NameEn           nvarchar(40) NULL,
    NameVi           nvarchar(40) NULL,
    BaseUnit         nvarchar(3)  NOT NULL,
    BaseUnitExternal nvarchar(3)  NULL,
    BaseUnitNameEn   nvarchar(30) NULL,
    BaseUnitNameVi   nvarchar(30) NULL,
    MaterialType     nvarchar(4)  NULL,
    MaterialGroup    nvarchar(9)  NULL,
    Division         nvarchar(2)  NULL,
    IsDeleted        bit          NOT NULL,
    CreatedOn        date         NULL,
    ChangedOn        date         NULL,
    SyncedAtUtc      datetime2    NOT NULL);
GO
CREATE PROCEDURE md.usp_Material_Replace @Rows md.MaterialTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.Material;
        INSERT INTO md.Material (MaterialNumber, MaterialCode, NameEn, NameVi, BaseUnit, BaseUnitExternal, BaseUnitNameEn, BaseUnitNameVi,
                                 MaterialType, MaterialGroup, Division, IsDeleted, CreatedOn, ChangedOn, SyncedAtUtc)
        SELECT MaterialNumber, MaterialCode, NameEn, NameVi, BaseUnit, BaseUnitExternal, BaseUnitNameEn, BaseUnitNameVi,
               MaterialType, MaterialGroup, Division, IsDeleted, CreatedOn, ChangedOn, SyncedAtUtc
        FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
GO

CREATE TYPE md.MaterialGroupTableType AS TABLE (
    Code nvarchar(9) NOT NULL PRIMARY KEY, NameEn nvarchar(40) NULL, NameVi nvarchar(40) NULL,
    LongNameEn nvarchar(60) NULL, LongNameVi nvarchar(60) NULL, SapData nvarchar(max) NULL, SyncedAtUtc datetime2 NOT NULL);
GO
CREATE PROCEDURE md.usp_MaterialGroup_Replace @Rows md.MaterialGroupTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.MaterialGroup;
        INSERT INTO md.MaterialGroup (Code, NameEn, NameVi, LongNameEn, LongNameVi, SapData, SyncedAtUtc)
        SELECT Code, NameEn, NameVi, LongNameEn, LongNameVi, SapData, SyncedAtUtc FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
GO

CREATE TYPE md.MaterialTypeTableType AS TABLE (
    Code nvarchar(4) NOT NULL PRIMARY KEY, NameEn nvarchar(40) NULL, NameVi nvarchar(40) NULL,
    SapData nvarchar(max) NULL, SyncedAtUtc datetime2 NOT NULL);
GO
CREATE PROCEDURE md.usp_MaterialType_Replace @Rows md.MaterialTypeTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.MaterialType;
        INSERT INTO md.MaterialType (Code, NameEn, NameVi, SapData, SyncedAtUtc) SELECT Code, NameEn, NameVi, SapData, SyncedAtUtc FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
GO

CREATE TYPE md.DivisionTableType AS TABLE (
    Code nvarchar(2) NOT NULL PRIMARY KEY, NameEn nvarchar(40) NULL, NameVi nvarchar(40) NULL, SyncedAtUtc datetime2 NOT NULL);
GO
CREATE PROCEDURE md.usp_Division_Replace @Rows md.DivisionTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.Division;
        INSERT INTO md.Division (Code, NameEn, NameVi, SyncedAtUtc) SELECT Code, NameEn, NameVi, SyncedAtUtc FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
