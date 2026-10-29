using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Rdm.Application.Postings;
using Rdm.Sap;

namespace Rdm.Tests;

public class PriceQueryTests
{
    private static List<ValidationResult> Validate(object o)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(o, new ValidationContext(o), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Sales_org_condition_type_and_validity_range_are_required()
    {
        var errors = Validate(new PriceQuery());

        errors.SelectMany(e => e.MemberNames).Should()
            .Contain(new[] { "ConditionType", "SalesOrganization", "ValidFrom", "ValidTo" });
    }

    [Fact]
    public void Valid_to_before_valid_from_is_rejected()
    {
        var q = new PriceQuery
        {
            ConditionType = "ZVLP", SalesOrganization = "G210",
            ValidFrom = new DateOnly(2026, 12, 31), ValidTo = new DateOnly(2026, 1, 1)
        };

        Validate(q).Should().ContainSingle(e => e.MemberNames.Contains("ValidTo"));

        q.ValidTo = new DateOnly(2026, 12, 31);
        Validate(q).Should().BeEmpty();
    }

    [Fact]
    public void A990_record_exposes_named_key_fields_and_processing_status()
    {
        var key = new PriceKey("ZVLP", PriceConditionRules.A990, new Dictionary<string, string>
        {
            ["VKORG"] = "G210", ["VTWEG"] = "10", ["KUNAG"] = "0000100166", ["KUNWE"] = "0000200379",
            ["MATNR"] = "000000000000100016", ["VSBED"] = "PK", ["WERKS"] = "G112"
        });
        var record = new PriceRecord("0000123456", key, "", "02", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            false, 1_500_000m, "VND", 1, "TO", "C");

        var dto = SapClient.ToDto(record);

        dto.ConditionTable.Should().Be("A990");
        dto.SalesOrganization.Should().Be("G210");
        dto.SoldTo.Should().Be("0000100166");
        dto.ShipTo.Should().Be("0000200379");
        dto.MaterialCode.Should().Be("100016");
        dto.ShippingCondition.Should().Be("PK");
        dto.Plant.Should().Be("G112");
        dto.Incoterms.Should().BeNull();
        dto.Region.Should().BeNull();
        dto.ProcessingStatus.Should().Be("02");
        dto.CalculationType.Should().Be("C");
        dto.PendingApproval.Should().BeFalse();
        dto.Amount.Should().Be(1_500_000m);
    }

    private static readonly string[] AllKeyProperties =
    {
        "salesOrganization", "distributionChannel", "soldTo", "shipTo", "materialCode", "shippingCondition", "plant", "incoterms", "region"
    };

    [Theory]
    [InlineData("990", "ZVLP")]
    [InlineData("991", "ZVIV")]
    [InlineData("912", "ZMIN")]
    [InlineData("919", "ZMIN")]
    public void Row_json_contains_exactly_the_key_columns_of_its_table(string tableNumber, string conditionType)
    {
        var table = PriceConditionRules.TableByNumber(tableNumber)!;
        var values = table.Fields.ToDictionary(f => f.Name, f => f.Name == "MATNR" ? "000000000000100016" : "X");
        var dto = SapClient.ToDto(new PriceRecord("1", new PriceKey(conditionType, table, values), "", "01",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), false, 1m, "VND", 1, "TO", "C"));

        var json = System.Text.Json.JsonSerializer.SerializeToElement(dto,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var present = AllKeyProperties.Where(p => json.TryGetProperty(p, out _)).ToList();

        var layout = PriceConditionRules.Layout(table);
        present.Should().BeEquivalentTo(layout.KeyColumns.Select(c => c.Name));
        json.TryGetProperty("key", out _).Should().BeFalse();
        var missingDetail = layout.DetailColumns.Select(c => c.Name).Where(n => !json.TryGetProperty(n, out _)).ToList();
        missingDetail.Should().BeEmpty();
    }

    [Fact]
    public void A991_sales_org_comes_from_order_sales_org()
    {
        var key = new PriceKey("ZVIV", PriceConditionRules.A991, new Dictionary<string, string>
        {
            ["VKORGAU"] = "G210", ["WERKS"] = "G112", ["MATNR"] = "000000000000100016"
        });
        var dto = SapClient.ToDto(new PriceRecord("1", key, "A", null, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            false, 100m, "VND", 1, "TO"));

        dto.SalesOrganization.Should().Be("G210");
        dto.SoldTo.Should().BeNull();
        dto.PendingApproval.Should().BeTrue();
    }
}
