using FluentAssertions;
using Rdm.Sap;

namespace Rdm.Tests;

public class CustomerCreditTests
{
    // Layout + dòng thật của KNKK khách 100166 / G110 trên DRF-930 (RFC_READ_TABLE, DELIMITER '|').
    private static readonly (string, int, int)[] Layout =
    {
        ("KUNNR", 0, 10), ("KKBER", 11, 4), ("KLIMK", 16, 15), ("KNKLI", 32, 10), ("SAUFT", 43, 15),
        ("SKFOR", 59, 15), ("SSOBL", 75, 15), ("UEDAT", 91, 8), ("ERNAM", 100, 12), ("ERDAT", 113, 8),
        ("CTLPC", 122, 3), ("KRAUS", 126, 11), ("AEDAT", 138, 8), ("AENAM", 147, 12), ("CASHD", 160, 8),
        ("CASHA", 169, 13), ("CASHC", 183, 5)
    };

    private const string Row =
        "0000100166|G110|          0.00 |0000100166|          0.00 |     112941.20-|     218071.72-|00000000|LINH.LY     |20220826|Z02|CASH       |20220826|SHCHIN      |20230710|     4120.80 |VND";

    private static readonly CurrencySettings Currencies = new(
        new Dictionary<string, string> { ["G110"] = "VND" },
        new Dictionary<string, int> { ["VND"] = 0 },
        DateTime.UtcNow);

    /// <summary>Dòng mẫu đọc bằng RFC_READ_TABLE — so với kết quả Z_ESD017 (CustomerCreditFunctionTests).</summary>
    internal Rdm.Application.Customers.CustomerCredit MapSampleRow() =>
        SapMasterDataClient.MapKnkk(RfcTableReader.Split(Row, Layout), Currencies);

    [Fact]
    public void Knkk_row_is_parsed_and_vnd_amounts_are_shifted_to_external_value()
    {
        var c = SapMasterDataClient.MapKnkk(RfcTableReader.Split(Row, Layout), Currencies);

        c.CustomerNumber.Should().Be("0000100166");
        c.CreditControlArea.Should().Be("G110");
        c.Currency.Should().Be("VND");
        c.CreditLimit.Should().Be(0m);
        c.Receivables.Should().Be(-11_294_120m);
        c.SpecialLiabilities.Should().Be(-21_807_172m);
        c.LastPaymentAmount.Should().Be(412_080m);
        c.LastPaymentCurrency.Should().Be("VND");
        c.LastPaymentDate.Should().Be(new DateOnly(2023, 7, 10));
        c.CreditLimitExceededOn.Should().BeNull();
        c.CreatedOn.Should().Be(new DateOnly(2022, 8, 26));
        c.RiskCategory.Should().Be("Z02");
        c.CreditInfoNumber.Should().Be("CASH");
        c.CreatedBy.Should().Be("LINH.LY");
    }

    [Fact]
    public void Trailing_spaces_trimmed_by_nco_do_not_break_last_columns()
    {
        var trimmed = Row[..183].TrimEnd(); // CASHC rỗng → NCo cắt khoảng trắng cuối WA
        var r = RfcTableReader.Split(trimmed, Layout);

        r["CASHC"].Should().BeEmpty();
        r["CASHA"].Should().Be("4120.80");
    }

    [Theory]
    [InlineData("     112941.20-", -112941.20)]
    [InlineData("  1124358.50 ", 1124358.50)]
    [InlineData("", 0)]
    public void Packed_numbers_with_trailing_sign_are_parsed(string raw, double expected) =>
        RfcTableReader.ParseDecimal(raw).Should().Be((decimal)expected);

    [Theory]
    [InlineData(2, 100.50, 100.50)]
    [InlineData(0, 112941.20, 11294120)]
    [InlineData(3, 12.34, 1.234)]
    public void Internal_amounts_are_converted_by_currency_decimals(int decimals, double stored, double expected) =>
        RfcTableReader.ToExternalAmount((decimal)stored, decimals).Should().Be((decimal)expected);

    [Theory]
    [InlineData("100166", "0000100166")]
    [InlineData(" 0000100166 ", "0000100166")]
    [InlineData("cust01", "CUST01")]
    public void Numeric_customer_numbers_are_zero_padded(string input, string expected) =>
        SapMasterDataClient.NormalizeCustomer(input).Should().Be(expected);

    [Fact]
    public void Condition_escapes_quotes()
    {
        RfcTableReader.Condition("KUNNR", "A'B", and: true).Should().Be("AND KUNNR = 'A''B'");
    }
}

public class CustomerCreditFunctionTests
{
    // Dòng ZSRDM_S_KNKK như NCo GetString trả cho khách 100166 / G110 (Z_ESD017 đã quy đổi số tiền theo TCURX).
    private static readonly Dictionary<string, string> Row = new()
    {
        ["KUNNR"] = "0000100166", ["KKBER"] = "G110", ["WAERS"] = "VND", ["KLIMK"] = "0.0000", ["KNKLI"] = "0000100166",
        ["SAUFT"] = "0.0000", ["SKFOR"] = "-11294120.0000", ["SSOBL"] = "-21807172.0000", ["UEDAT"] = "0000-00-00",
        ["ERNAM"] = "LINH.LY", ["ERDAT"] = "2022-08-26", ["CTLPC"] = "Z02", ["KRAUS"] = "CASH", ["AEDAT"] = "20220826",
        ["AENAM"] = "SHCHIN", ["CASHD"] = "2023-07-10", ["CASHA"] = "412080.0000", ["CASHC"] = "VND"
    };

    [Fact]
    public void Function_row_keeps_external_amounts_and_parses_both_date_formats()
    {
        var c = Rdm.Sap.SapMasterDataClient.MapCreditRow(Row);

        c.CustomerNumber.Should().Be("0000100166");
        c.Currency.Should().Be("VND");
        c.Receivables.Should().Be(-11_294_120m);
        c.SpecialLiabilities.Should().Be(-21_807_172m);
        c.LastPaymentAmount.Should().Be(412_080m);
        c.LastPaymentAmount.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be("412080");
        c.CreditLimitExceededOn.Should().BeNull();
        c.CreatedOn.Should().Be(new DateOnly(2022, 8, 26));
        c.ChangedOn.Should().Be(new DateOnly(2022, 8, 26));
        c.LastPaymentDate.Should().Be(new DateOnly(2023, 7, 10));
        c.RiskCategory.Should().Be("Z02");
    }

    [Fact]
    public void Function_and_read_table_paths_return_the_same_record()
    {
        var byTable = new CustomerCreditTests().MapSampleRow();
        var byFunction = Rdm.Sap.SapMasterDataClient.MapCreditRow(Row);

        byFunction.Should().BeEquivalentTo(byTable);
    }

    [Theory]
    [InlineData("E", "CREDIT_CUSTOMER_NOT_FOUND", "CREDIT_CUSTOMER_NOT_FOUND")]
    [InlineData("E", "CREDIT_NO_AUTHORITY", "CREDIT_NO_AUTHORITY")]
    [InlineData("A", "", "SAP_BUSINESS")]
    public void Error_messages_become_read_exceptions_with_rdm_codes(string type, string code, string expected)
    {
        var ex = Rdm.Sap.SapMasterDataClient.InterpretCreditMessages(
            new[] { new Rdm.Sap.SapMasterDataClient.CreditMessage(type, code, "KUNNR", "Khách hàng 0000999999 không tồn tại (KNA1).") },
            "Z_ESD017_READ_KNKK");

        ex.Should().NotBeNull();
        ex!.ErrorCode.Should().Be(expected);
        ex.Message.Should().Contain("không tồn tại");
    }

    [Fact]
    public void Info_and_warning_messages_are_not_errors()
    {
        Rdm.Sap.SapMasterDataClient.InterpretCreditMessages(new[]
        {
            new Rdm.Sap.SapMasterDataClient.CreditMessage("I", "CREDIT_NOT_FOUND", "", "Không có dữ liệu KNKK phù hợp."),
            new Rdm.Sap.SapMasterDataClient.CreditMessage("W", "CREDIT_NO_AUTHORITY", "KKBER", "Bỏ qua credit control area G100")
        }, "Z_ESD017_READ_KNKK").Should().BeNull();
    }
}
