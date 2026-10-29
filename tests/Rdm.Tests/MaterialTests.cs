using FluentAssertions;
using Rdm.Sap;

namespace Rdm.Tests;

public class MaterialTests
{
    private const string En = "E";
    private const string Vi = "쁩";

    private static Dictionary<string, string> Mara(string matnr, string meins = "TO", string lvorm = "") => new()
    {
        ["MATNR"] = matnr, ["MTART"] = "ZFER", ["MATKL"] = "10100", ["SPART"] = "10", ["MEINS"] = meins,
        ["LVORM"] = lvorm, ["ERSDA"] = "20200101", ["LAEDA"] = "00000000"
    };

    private static Dictionary<string, string> Makt(string matnr, string spras, string text) => new()
    {
        ["MATNR"] = matnr, ["SPRAS"] = spras, ["MAKTX"] = text
    };

    private static Dictionary<string, string> Unit(string spras, string msehi, string mseh3, string msehl) => new()
    {
        ["SPRAS"] = spras, ["MSEHI"] = msehi, ["MSEH3"] = mseh3, ["MSEHL"] = msehl
    };

    [Fact]
    public void Material_gets_en_vi_names_and_external_unit()
    {
        // Dữ liệu thật trên DRF-930 (MAKT có cả ngôn ngữ 'Z' — bị bỏ qua).
        var result = SapMasterDataClient.BuildMaterials(
            new[] { Mara("000000000000100016") },
            new[]
            {
                Makt("000000000000100016", En, "Bag cement PCB40"),
                Makt("000000000000100016", "Z", "Xi măng FICO -YTL bao xanh dương"),
                Makt("000000000000100016", Vi, "Xi măng Fico PCB40 bao")
            },
            new[] { Unit(En, "TO", "MT", "Metric Tonne"), Unit(Vi, "TO", "MT", "Tấn") },
            En, Vi);

        var m = result.Should().ContainSingle().Subject;
        m.MaterialNumber.Should().Be("000000000000100016");
        m.MaterialCode.Should().Be("100016");
        m.NameEn.Should().Be("Bag cement PCB40");
        m.NameVi.Should().Be("Xi măng Fico PCB40 bao");
        m.BaseUnit.Should().Be("TO");
        m.BaseUnitExternal.Should().Be("MT");
        m.BaseUnitNameEn.Should().Be("Metric Tonne");
        m.BaseUnitNameVi.Should().Be("Tấn");
        m.Division.Should().Be("10");
        m.CreatedOn.Should().Be(new DateOnly(2020, 1, 1));
        m.ChangedOn.Should().BeNull();
        m.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Deleted_flag_and_duplicate_rows_are_handled()
    {
        var result = SapMasterDataClient.BuildMaterials(
            new[] { Mara("X1", lvorm: "X"), Mara("X1", lvorm: "X") }, Array.Empty<Dictionary<string, string>>(),
            Array.Empty<Dictionary<string, string>>(), En, Vi);

        result.Should().ContainSingle().Which.IsDeleted.Should().BeTrue();
        result[0].NameEn.Should().BeNull();
    }

    [Fact]
    public void Material_filter_defaults_to_zfnp_only()
    {
        SapMasterDataClient.MaterialTypeFilter(new Rdm.Sap.SapOptions().MaterialTypes)
            .Should().Equal("MTART = 'ZFNP'");
    }

    [Fact]
    public void Material_filter_with_several_types_is_or_grouped()
    {
        SapMasterDataClient.MaterialTypeFilter(new[] { "zfnp", "ZHWA" })
            .Should().Equal("( MTART = 'ZFNP'", "OR MTART = 'ZHWA' )");
        SapMasterDataClient.MaterialTypeFilter(Array.Empty<string>()).Should().BeEmpty();
    }

    [Fact]
    public void Plant_filter_defaults_to_vietnam_only()
    {
        RfcTableReader.AnyOf("LAND1", new Rdm.Sap.SapOptions().PlantCountries).Should().Equal("LAND1 = 'VN'");
    }

    [Theory]
    [InlineData("000000000000100016", "100016")]
    [InlineData("301423R2", "301423R2")]
    [InlineData("000000000000000000", "0")]
    public void External_material_code_strips_leading_zeros_only_for_numeric(string matnr, string expected) =>
        SapMasterDataClient.ExternalMaterialCode(matnr).Should().Be(expected);
}
