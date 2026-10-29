-- Master data SD: mỗi bảng 1 table type (TVP) + 1 procedure thay toàn bộ dữ liệu.
-- Thứ tự cột của TVP phải khớp MasterDataRepository.Targets.
-- Batch tách bằng dòng "GO".

CREATE TYPE md.SoldToTableType AS TABLE (
    CustomerNumber        nvarchar(10)  NOT NULL PRIMARY KEY,
    CustomerName          nvarchar(120) NOT NULL,
    Name1                 nvarchar(35)  NULL,
    Name2                 nvarchar(35)  NULL,
    Name3                 nvarchar(35)  NULL,
    Name4                 nvarchar(35)  NULL,
    SearchTerm            nvarchar(10)  NULL,
    TaxNumber             nvarchar(20)  NULL,
    TaxNumber1            nvarchar(16)  NULL,
    VatRegistrationNumber nvarchar(20)  NULL,
    CustomerType          nvarchar(4)   NOT NULL,
    CustomerTypeName      nvarchar(30)  NULL,
    CreatedOn             date          NULL,
    SyncedAtUtc           datetime2     NOT NULL);
GO
CREATE PROCEDURE md.usp_SoldTo_Replace @Rows md.SoldToTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.SoldTo;
        INSERT INTO md.SoldTo (CustomerNumber, CustomerName, Name1, Name2, Name3, Name4, SearchTerm, TaxNumber, TaxNumber1,
                               VatRegistrationNumber, CustomerType, CustomerTypeName, CreatedOn, SyncedAtUtc)
        SELECT CustomerNumber, CustomerName, Name1, Name2, Name3, Name4, SearchTerm, TaxNumber, TaxNumber1,
               VatRegistrationNumber, CustomerType, CustomerTypeName, CreatedOn, SyncedAtUtc
        FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
GO

CREATE TYPE md.ShipToTableType AS TABLE (
    SoldTo              nvarchar(10)  NOT NULL,
    SalesOrg            nvarchar(4)   NOT NULL,
    DistributionChannel nvarchar(2)   NOT NULL,
    Division            nvarchar(2)   NOT NULL,
    PartnerCounter      int           NOT NULL,
    ShipToNumber        nvarchar(10)  NOT NULL,
    IsDefault           bit           NOT NULL,
    CustomerName        nvarchar(120) NOT NULL,
    Name1               nvarchar(35)  NULL,
    Name2               nvarchar(35)  NULL,
    Name3               nvarchar(35)  NULL,
    Name4               nvarchar(35)  NULL,
    Street              nvarchar(35)  NULL,
    City                nvarchar(35)  NULL,
    PostalCode          nvarchar(10)  NULL,
    Region              nvarchar(3)   NULL,
    Country             nvarchar(3)   NULL,
    Phone               nvarchar(16)  NULL,
    TaxNumber           nvarchar(20)  NULL,
    SyncedAtUtc         datetime2     NOT NULL,
    PRIMARY KEY (SoldTo, SalesOrg, DistributionChannel, Division, PartnerCounter));
GO
CREATE PROCEDURE md.usp_ShipTo_Replace @Rows md.ShipToTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.ShipTo;
        INSERT INTO md.ShipTo (SoldTo, SalesOrg, DistributionChannel, Division, PartnerCounter, ShipToNumber, IsDefault, CustomerName,
                               Name1, Name2, Name3, Name4, Street, City, PostalCode, Region, Country, Phone, TaxNumber, SyncedAtUtc)
        SELECT SoldTo, SalesOrg, DistributionChannel, Division, PartnerCounter, ShipToNumber, IsDefault, CustomerName,
               Name1, Name2, Name3, Name4, Street, City, PostalCode, Region, Country, Phone, TaxNumber, SyncedAtUtc
        FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
GO

CREATE TYPE md.SalesDistrictTableType AS TABLE (
    Code nvarchar(6) NOT NULL PRIMARY KEY, NameEn nvarchar(40) NULL, NameVi nvarchar(40) NULL, SyncedAtUtc datetime2 NOT NULL);
GO
CREATE PROCEDURE md.usp_SalesDistrict_Replace @Rows md.SalesDistrictTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.SalesDistrict;
        INSERT INTO md.SalesDistrict (Code, NameEn, NameVi, SyncedAtUtc) SELECT Code, NameEn, NameVi, SyncedAtUtc FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
GO

CREATE TYPE md.ShippingConditionTableType AS TABLE (
    Code nvarchar(2) NOT NULL PRIMARY KEY, NameEn nvarchar(40) NULL, NameVi nvarchar(40) NULL, SyncedAtUtc datetime2 NOT NULL);
GO
CREATE PROCEDURE md.usp_ShippingCondition_Replace @Rows md.ShippingConditionTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.ShippingCondition;
        INSERT INTO md.ShippingCondition (Code, NameEn, NameVi, SyncedAtUtc) SELECT Code, NameEn, NameVi, SyncedAtUtc FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
GO

CREATE TYPE md.CustomerGroup3TableType AS TABLE (
    Code nvarchar(3) NOT NULL PRIMARY KEY, NameEn nvarchar(40) NULL, NameVi nvarchar(40) NULL, SyncedAtUtc datetime2 NOT NULL);
GO
CREATE PROCEDURE md.usp_CustomerGroup3_Replace @Rows md.CustomerGroup3TableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.CustomerGroup3;
        INSERT INTO md.CustomerGroup3 (Code, NameEn, NameVi, SyncedAtUtc) SELECT Code, NameEn, NameVi, SyncedAtUtc FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
GO

CREATE TYPE md.SalesOfficeTableType AS TABLE (
    Code nvarchar(4) NOT NULL PRIMARY KEY, NameEn nvarchar(40) NULL, NameVi nvarchar(40) NULL,
    SapData nvarchar(max) NULL, SyncedAtUtc datetime2 NOT NULL);
GO
CREATE PROCEDURE md.usp_SalesOffice_Replace @Rows md.SalesOfficeTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.SalesOffice;
        INSERT INTO md.SalesOffice (Code, NameEn, NameVi, SapData, SyncedAtUtc) SELECT Code, NameEn, NameVi, SapData, SyncedAtUtc FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
GO

CREATE TYPE md.SalesOfficeAssignmentTableType AS TABLE (
    SalesOrg nvarchar(4) NOT NULL, DistributionChannel nvarchar(2) NOT NULL, Division nvarchar(2) NOT NULL,
    SalesOffice nvarchar(4) NOT NULL, SyncedAtUtc datetime2 NOT NULL,
    PRIMARY KEY (SalesOrg, DistributionChannel, Division, SalesOffice));
GO
CREATE PROCEDURE md.usp_SalesOfficeAssignment_Replace @Rows md.SalesOfficeAssignmentTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.SalesOfficeAssignment;
        INSERT INTO md.SalesOfficeAssignment (SalesOrg, DistributionChannel, Division, SalesOffice, SyncedAtUtc)
        SELECT SalesOrg, DistributionChannel, Division, SalesOffice, SyncedAtUtc FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
GO

CREATE TYPE md.SalesOrgTableType AS TABLE (
    Code nvarchar(4) NOT NULL PRIMARY KEY, NameEn nvarchar(40) NULL, NameVi nvarchar(40) NULL,
    Currency nvarchar(5) NULL, CompanyCode nvarchar(4) NULL, SapData nvarchar(max) NULL, SyncedAtUtc datetime2 NOT NULL);
GO
CREATE PROCEDURE md.usp_SalesOrg_Replace @Rows md.SalesOrgTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.SalesOrg;
        INSERT INTO md.SalesOrg (Code, NameEn, NameVi, Currency, CompanyCode, SapData, SyncedAtUtc)
        SELECT Code, NameEn, NameVi, Currency, CompanyCode, SapData, SyncedAtUtc FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
GO

CREATE TYPE md.VehicleTypeTableType AS TABLE (
    Code nvarchar(2) NOT NULL PRIMARY KEY, NameEn nvarchar(40) NULL, NameVi nvarchar(40) NULL,
    SapData nvarchar(max) NULL, SyncedAtUtc datetime2 NOT NULL);
GO
CREATE PROCEDURE md.usp_VehicleType_Replace @Rows md.VehicleTypeTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.VehicleType;
        INSERT INTO md.VehicleType (Code, NameEn, NameVi, SapData, SyncedAtUtc) SELECT Code, NameEn, NameVi, SapData, SyncedAtUtc FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
GO

CREATE TYPE md.PlantTableType AS TABLE (
    Code nvarchar(4) NOT NULL PRIMARY KEY, Name1 nvarchar(30) NULL, Name2 nvarchar(30) NULL, SalesOrg nvarchar(4) NULL,
    DistributionChannel nvarchar(2) NULL, Division nvarchar(2) NULL, Street nvarchar(30) NULL, City nvarchar(25) NULL,
    PostalCode nvarchar(10) NULL, Region nvarchar(3) NULL, Country nvarchar(3) NULL, SapData nvarchar(max) NULL,
    SyncedAtUtc datetime2 NOT NULL);
GO
CREATE PROCEDURE md.usp_Plant_Replace @Rows md.PlantTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.Plant;
        INSERT INTO md.Plant (Code, Name1, Name2, SalesOrg, DistributionChannel, Division, Street, City, PostalCode, Region, Country, SapData, SyncedAtUtc)
        SELECT Code, Name1, Name2, SalesOrg, DistributionChannel, Division, Street, City, PostalCode, Region, Country, SapData, SyncedAtUtc
        FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
GO

CREATE TYPE md.PaymentMethodTableType AS TABLE (
    Country nvarchar(3) NOT NULL, Code nvarchar(1) NOT NULL, Description nvarchar(30) NULL,
    SapData nvarchar(max) NULL, SyncedAtUtc datetime2 NOT NULL, PRIMARY KEY (Country, Code));
GO
CREATE PROCEDURE md.usp_PaymentMethod_Replace @Rows md.PaymentMethodTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.PaymentMethod;
        INSERT INTO md.PaymentMethod (Country, Code, Description, SapData, SyncedAtUtc)
        SELECT Country, Code, Description, SapData, SyncedAtUtc FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
GO

CREATE PROCEDURE md.usp_SyncRun_Insert
    @Object nvarchar(32), @Trigger nvarchar(16), @StartedAtUtc datetime2, @CompletedAtUtc datetime2,
    @DurationMs int, @Success bit, @RowCount int, @Error nvarchar(2000)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO md.SyncRun ([Object], [Trigger], StartedAtUtc, CompletedAtUtc, DurationMs, Success, [RowCount], Error)
    VALUES (@Object, @Trigger, @StartedAtUtc, @CompletedAtUtc, @DurationMs, @Success, @RowCount, @Error);
    SELECT CAST(SCOPE_IDENTITY() AS bigint) AS Id;
END
