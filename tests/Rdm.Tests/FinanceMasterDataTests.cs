using FluentAssertions;
using Rdm.Sap;

namespace Rdm.Tests;

public class FinanceMasterDataTests
{
    private static Dictionary<string, string> Row(params (string Key, string Value)[] kv) =>
        kv.ToDictionary(x => x.Key, x => x.Value);

    [Fact]
    public void Company_codes_are_coa_1000_intersected_with_co_area_g100()
    {
        // DRF-930: COA 1000 dùng cho cả Malaysia / Singapore; CO area G100 chỉ gán 4 company code Việt Nam.
        var t001 = new[] { "1000", "2110", "C420", "G110", "G120", "G130", "G210" }.Select(b => Row(("BUKRS", b), ("KTOPL", "1000"))).ToList();
        var tka02 = new[] { "G210", "G110", "G120", "G130", "G999" }.Select(b => Row(("KOKRS", "G100"), ("BUKRS", b))).ToList();

        SapMasterDataClient.ResolveCompanyCodes(t001, tka02).Should().Equal("G110", "G120", "G130", "G210");
    }

    [Theory]
    [InlineData("0011111000", "11111000")]
    [InlineData("G11031104", "G11031104")]
    public void Account_code_strips_leading_zeros_only_for_numeric(string code, string expected) =>
        SapMasterDataClient.ExternalCode(code).Should().Be(expected);

    [Fact]
    public void Default_scope_is_coa_1000_and_co_area_g100()
    {
        var o = new SapOptions();
        o.ChartOfAccounts.Should().Be("1000");
        o.ControllingArea.Should().Be("G100");
    }
}
