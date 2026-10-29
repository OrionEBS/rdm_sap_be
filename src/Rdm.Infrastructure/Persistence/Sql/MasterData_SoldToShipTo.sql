-- Tách ship-to thành danh mục (md.ShipTo, 1 dòng / ship-to) + bảng quan hệ md.SoldToShipTo.
-- Định nghĩa ở đây thay thế md.ShipToTableType / md.usp_ShipTo_Replace của MasterData_Procedures.sql.

CREATE TYPE md.ShipToTableType AS TABLE (
    CustomerNumber   nvarchar(10)  NOT NULL PRIMARY KEY,
    CustomerName     nvarchar(120) NOT NULL,
    Name1            nvarchar(35)  NULL,
    Name2            nvarchar(35)  NULL,
    Name3            nvarchar(35)  NULL,
    Name4            nvarchar(35)  NULL,
    Street           nvarchar(35)  NULL,
    City             nvarchar(35)  NULL,
    PostalCode       nvarchar(10)  NULL,
    Region           nvarchar(3)   NULL,
    Country          nvarchar(3)   NULL,
    Phone            nvarchar(16)  NULL,
    SearchTerm       nvarchar(10)  NULL,
    TaxNumber        nvarchar(20)  NULL,
    CustomerType     nvarchar(4)   NOT NULL,
    CustomerTypeName nvarchar(30)  NULL,
    SyncedAtUtc      datetime2     NOT NULL);
GO
CREATE PROCEDURE md.usp_ShipTo_Replace @Rows md.ShipToTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.ShipTo;
        INSERT INTO md.ShipTo (CustomerNumber, CustomerName, Name1, Name2, Name3, Name4, Street, City, PostalCode, Region, Country,
                               Phone, SearchTerm, TaxNumber, CustomerType, CustomerTypeName, SyncedAtUtc)
        SELECT CustomerNumber, CustomerName, Name1, Name2, Name3, Name4, Street, City, PostalCode, Region, Country,
               Phone, SearchTerm, TaxNumber, CustomerType, CustomerTypeName, SyncedAtUtc
        FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
GO

CREATE TYPE md.SoldToShipToTableType AS TABLE (
    SoldTo              nvarchar(10) NOT NULL,
    SalesOrg            nvarchar(4)  NOT NULL,
    DistributionChannel nvarchar(2)  NOT NULL,
    Division            nvarchar(2)  NOT NULL,
    PartnerCounter      int          NOT NULL,
    ShipTo              nvarchar(10) NOT NULL,
    IsDefault           bit          NOT NULL,
    SyncedAtUtc         datetime2    NOT NULL,
    PRIMARY KEY (SoldTo, SalesOrg, DistributionChannel, Division, PartnerCounter));
GO
CREATE PROCEDURE md.usp_SoldToShipTo_Replace @Rows md.SoldToShipToTableType READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @n int;
    BEGIN TRAN;
        DELETE FROM md.SoldToShipTo;
        INSERT INTO md.SoldToShipTo (SoldTo, SalesOrg, DistributionChannel, Division, PartnerCounter, ShipTo, IsDefault, SyncedAtUtc)
        SELECT SoldTo, SalesOrg, DistributionChannel, Division, PartnerCounter, ShipTo, IsDefault, SyncedAtUtc
        FROM @Rows;
        SET @n = @@ROWCOUNT;
    COMMIT;
    SELECT @n AS [RowCount];
END
