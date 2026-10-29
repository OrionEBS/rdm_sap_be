using Microsoft.EntityFrameworkCore;
using Rdm.Application.ApiCatalog;
using Rdm.Application.Auth;
using Rdm.Application.MasterData;
using Rdm.Application.Postings;

namespace Rdm.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public const string Schema = "rdm";

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<SapPosting> SapPostings => Set<SapPosting>();
    public DbSet<ApiUser> ApiUsers => Set<ApiUser>();
    public DbSet<ApiEndpointSetting> ApiEndpoints => Set<ApiEndpointSetting>();
    public DbSet<ApiOutputProfile> ApiOutputProfiles => Set<ApiOutputProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<SapPosting>(e =>
        {
            e.ToTable("SapPostings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Operation).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.RequestId).HasMaxLength(64).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsUnicode(false).IsRequired();
            e.Property(x => x.RequestJson).IsRequired();
            e.Property(x => x.DocumentNumber).HasMaxLength(20);
            e.Property(x => x.CompanyCode).HasMaxLength(4);
            e.Property(x => x.FiscalYear).HasMaxLength(4);
            e.Property(x => x.ObjectType).HasMaxLength(10);
            e.Property(x => x.ObjectKey).HasMaxLength(20);
            e.Property(x => x.ObjectSystem).HasMaxLength(10);
            e.Property(x => x.ErrorCode).HasMaxLength(32);
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);
            e.Property(x => x.IsBackgroundJob).HasDefaultValue(false);

            // Khóa idempotency — chặn 2 request song song cùng requestId ở tầng DB.
            e.HasIndex(x => new { x.Operation, x.RequestId }).IsUnique();
            e.HasIndex(x => new { x.Status, x.CreatedAtUtc });
            e.HasIndex(x => x.DocumentNumber);
        });

        // Bảng cấu hình active / inactive API — ghi qua rdm.usp_ApiEndpoint_* (Sql/ApiEndpoints.sql).
        modelBuilder.Entity<ApiEndpointSetting>(e =>
        {
            e.ToTable("ApiEndpoints");
            e.HasKey(x => x.EndpointKey);
            e.Property(x => x.EndpointKey).HasMaxLength(300);
            e.Property(x => x.HttpMethod).HasMaxLength(10).IsRequired();
            e.Property(x => x.RouteTemplate).HasMaxLength(256).IsRequired();
            e.Property(x => x.GroupName).HasMaxLength(100).IsRequired();
            e.Property(x => x.ActionName).HasMaxLength(100).IsRequired();
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.PublicSummary).HasMaxLength(200);
            e.Property(x => x.PublicDescription).HasMaxLength(2000);
            e.Property(x => x.DocUpdatedBy).HasMaxLength(100);
            e.Property(x => x.PublicHidden).HasDefaultValue(false);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.UpdatedBy).HasMaxLength(100);
            e.HasIndex(x => x.GroupName);
        });

        // Cấu hình cột output / API sao chép — ghi qua rdm.usp_ApiOutputProfile_* (Sql/ApiOutputProfiles.sql).
        modelBuilder.Entity<ApiOutputProfile>(e =>
        {
            e.ToTable("ApiOutputProfiles");
            e.HasKey(x => x.ProfileId);
            e.Property(x => x.EndpointKey).HasMaxLength(300).IsRequired();
            e.Property(x => x.CopyName).HasMaxLength(60);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.FieldsJson).IsRequired();
            e.Property(x => x.UpdatedBy).HasMaxLength(100);
            e.HasIndex(x => x.EndpointKey).IsUnique().HasFilter("[CopyName] IS NULL").HasDatabaseName("UX_ApiOutputProfiles_Original");
            e.HasIndex(x => x.CopyName).IsUnique().HasFilter("[CopyName] IS NOT NULL").HasDatabaseName("UX_ApiOutputProfiles_CopyName");
        });

        modelBuilder.Entity<ApiUser>(e =>
        {
            e.ToTable("ApiUsers");
            e.HasKey(x => x.Username);
            e.Property(x => x.Username).HasMaxLength(64);
            e.Property(x => x.PasswordHash).HasMaxLength(500).IsRequired();
            e.Property(x => x.Role).HasMaxLength(32).IsRequired();
        });

        ConfigureMasterData(modelBuilder);
    }

    public const string MasterDataSchema = "md";

    private static void ConfigureMasterData(ModelBuilder b)
    {
        b.Entity<SoldToParty>(e =>
        {
            e.ToTable("SoldTo", MasterDataSchema);
            e.HasKey(x => x.CustomerNumber);
            e.Property(x => x.CustomerNumber).HasMaxLength(10);
            e.Property(x => x.CustomerName).HasMaxLength(120);
            e.Property(x => x.Name1).HasMaxLength(35);
            e.Property(x => x.Name2).HasMaxLength(35);
            e.Property(x => x.Name3).HasMaxLength(35);
            e.Property(x => x.Name4).HasMaxLength(35);
            e.Property(x => x.SearchTerm).HasMaxLength(10);
            e.Property(x => x.TaxNumber).HasMaxLength(20);
            e.Property(x => x.TaxNumber1).HasMaxLength(16);
            e.Property(x => x.VatRegistrationNumber).HasMaxLength(20);
            e.Property(x => x.CustomerType).HasMaxLength(4);
            e.Property(x => x.CustomerTypeName).HasMaxLength(30);
            e.HasIndex(x => x.TaxNumber);
        });

        b.Entity<ShipToParty>(e =>
        {
            e.ToTable("ShipTo", MasterDataSchema);
            e.HasKey(x => x.CustomerNumber);
            e.Property(x => x.CustomerNumber).HasMaxLength(10);
            e.Property(x => x.CustomerName).HasMaxLength(120);
            e.Property(x => x.SearchTerm).HasMaxLength(10);
            e.Property(x => x.CustomerType).HasMaxLength(4);
            e.Property(x => x.CustomerTypeName).HasMaxLength(30);
            e.Property(x => x.Name1).HasMaxLength(35);
            e.Property(x => x.Name2).HasMaxLength(35);
            e.Property(x => x.Name3).HasMaxLength(35);
            e.Property(x => x.Name4).HasMaxLength(35);
            e.Property(x => x.Street).HasMaxLength(35);
            e.Property(x => x.City).HasMaxLength(35);
            e.Property(x => x.PostalCode).HasMaxLength(10);
            e.Property(x => x.Region).HasMaxLength(3);
            e.Property(x => x.Country).HasMaxLength(3);
            e.Property(x => x.Phone).HasMaxLength(16);
            e.Property(x => x.TaxNumber).HasMaxLength(20);
        });

        b.Entity<SoldToShipTo>(e =>
        {
            e.ToTable("SoldToShipTo", MasterDataSchema);
            e.HasKey(x => new { x.SoldTo, x.SalesOrg, x.DistributionChannel, x.Division, x.PartnerCounter });
            e.Property(x => x.SoldTo).HasMaxLength(10);
            e.Property(x => x.SalesOrg).HasMaxLength(4);
            e.Property(x => x.DistributionChannel).HasMaxLength(2);
            e.Property(x => x.Division).HasMaxLength(2);
            e.Property(x => x.ShipTo).HasMaxLength(10);
            e.HasIndex(x => x.ShipTo);
        });

        CodeText<SalesDistrict>(b, "SalesDistrict", 6);
        CodeText<ShippingCondition>(b, "ShippingCondition", 2);
        CodeText<CustomerGroup3>(b, "CustomerGroup3", 3);
        CodeText<SalesOffice>(b, "SalesOffice", 4);
        CodeText<VehicleType>(b, "VehicleType", 2);
        CodeText<SalesOrganization>(b, "SalesOrg", 4, e =>
        {
            e.Property(x => x.Currency).HasMaxLength(5);
            e.Property(x => x.CompanyCode).HasMaxLength(4);
        });

        CodeText<MaterialType>(b, "MaterialType", 4);
        CodeText<Division>(b, "Division", 2);
        CodeText<MaterialGroup>(b, "MaterialGroup", 9, e =>
        {
            e.Property(x => x.LongNameEn).HasMaxLength(60);
            e.Property(x => x.LongNameVi).HasMaxLength(60);
        });

        b.Entity<Material>(e =>
        {
            e.ToTable("Material", MasterDataSchema);
            e.HasKey(x => x.MaterialNumber);
            e.Property(x => x.MaterialNumber).HasMaxLength(18);
            e.Property(x => x.MaterialCode).HasMaxLength(18);
            e.Property(x => x.NameEn).HasMaxLength(40);
            e.Property(x => x.NameVi).HasMaxLength(40);
            e.Property(x => x.BaseUnit).HasMaxLength(3);
            e.Property(x => x.BaseUnitExternal).HasMaxLength(3);
            e.Property(x => x.BaseUnitNameEn).HasMaxLength(30);
            e.Property(x => x.BaseUnitNameVi).HasMaxLength(30);
            e.Property(x => x.MaterialType).HasMaxLength(4);
            e.Property(x => x.MaterialGroup).HasMaxLength(9);
            e.Property(x => x.Division).HasMaxLength(2);
            e.HasIndex(x => x.MaterialCode);
            e.HasIndex(x => x.MaterialType);
            e.HasIndex(x => x.MaterialGroup);
        });

        b.Entity<GlAccount>(e =>
        {
            e.ToTable("GlAccount", MasterDataSchema);
            e.HasKey(x => new { x.CompanyCode, x.AccountNumber });
            e.Property(x => x.CompanyCode).HasMaxLength(4);
            e.Property(x => x.AccountNumber).HasMaxLength(10);
            e.Property(x => x.AccountCode).HasMaxLength(10);
            e.Property(x => x.ChartOfAccounts).HasMaxLength(4);
            e.Property(x => x.ShortText).HasMaxLength(20);
            e.Property(x => x.LongText).HasMaxLength(50);
            e.HasIndex(x => x.AccountCode);
        });

        b.Entity<CostCenter>(e =>
        {
            e.ToTable("CostCenter", MasterDataSchema);
            e.HasKey(x => new { x.ControllingArea, x.Code });
            e.Property(x => x.ControllingArea).HasMaxLength(4);
            e.Property(x => x.Code).HasMaxLength(10);
            e.Property(x => x.Name).HasMaxLength(20);
            e.Property(x => x.Description).HasMaxLength(40);
            e.Property(x => x.ActivationStatus).HasMaxLength(1);
        });

        b.Entity<SalesOfficeAssignment>(e =>
        {
            e.ToTable("SalesOfficeAssignment", MasterDataSchema);
            e.HasKey(x => new { x.SalesOrg, x.DistributionChannel, x.Division, x.SalesOffice });
            e.Property(x => x.SalesOrg).HasMaxLength(4);
            e.Property(x => x.DistributionChannel).HasMaxLength(2);
            e.Property(x => x.Division).HasMaxLength(2);
            e.Property(x => x.SalesOffice).HasMaxLength(4);
        });

        b.Entity<Plant>(e =>
        {
            e.ToTable("Plant", MasterDataSchema);
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(4);
            e.Property(x => x.Name1).HasMaxLength(30);
            e.Property(x => x.Name2).HasMaxLength(30);
            e.Property(x => x.SalesOrg).HasMaxLength(4);
            e.Property(x => x.DistributionChannel).HasMaxLength(2);
            e.Property(x => x.Division).HasMaxLength(2);
            e.Property(x => x.Street).HasMaxLength(30);
            e.Property(x => x.City).HasMaxLength(25);
            e.Property(x => x.PostalCode).HasMaxLength(10);
            e.Property(x => x.Region).HasMaxLength(3);
            e.Property(x => x.Country).HasMaxLength(3);
        });

        b.Entity<PaymentMethod>(e =>
        {
            e.ToTable("PaymentMethod", MasterDataSchema);
            e.HasKey(x => new { x.Country, x.Code });
            e.Property(x => x.Country).HasMaxLength(3);
            e.Property(x => x.Code).HasMaxLength(1);
            e.Property(x => x.Description).HasMaxLength(30);
        });

        b.Entity<MasterDataSyncRun>(e =>
        {
            e.ToTable("SyncRun", MasterDataSchema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Object).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Trigger).HasMaxLength(16);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => new { x.Object, x.StartedAtUtc });
        });
    }

    private static void CodeText<T>(ModelBuilder b, string table, int codeLength,
        Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T>>? extra = null) where T : CodeTextRow
    {
        b.Entity<T>(e =>
        {
            e.ToTable(table, MasterDataSchema);
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(codeLength);
            e.Property(x => x.NameEn).HasMaxLength(40);
            e.Property(x => x.NameVi).HasMaxLength(40);
            extra?.Invoke(e);
        });
    }
}
