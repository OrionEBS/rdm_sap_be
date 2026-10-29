using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Rdm.Application.Postings;
using Rdm.Application.Requests;
using Rdm.Sap;

namespace Rdm.Tests;

public class FreeGoodsTests
{
    private static Vbn1Request ValidVbn1() => new()
    {
        RequestId = "VBN1-1",
        KeyCombination = "1",
        SalesOrganization = "g210",
        DistributionChannel = "10",
        Customer = "100166",
        ValidFrom = new DateOnly(2026, 11, 1),
        ValidTo = new DateOnly(2026, 12, 31),
        Material = "fg001",
        Plant = "1000",
        ShippingCondition = "01",
        MinimumQuantity = 10,
        FreeGoodsQuantity = 10,
        QuantityUnit = "kg",
        AdditionalQuantity = 1,
        AdditionalQuantityUnit = "kg",
        CalculationRule = 1,
        FreeGoodsCategory = "1"
    };

    private static List<ValidationResult> Validate(object o)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(o, new ValidationContext(o), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Vbn1_maps_dates_codes_and_numc_calculation_rule()
    {
        var f = SapRequestMapper.ToVbn1(ValidVbn1());

        f["KSCHL"].Should().Be("ZA00");
        f["SELKZ"].Should().Be("1");
        f["VKORG"].Should().Be("G210");
        f["DATAB"].Should().Be("01.11.2026");
        f["DATBI"].Should().Be("31.12.2026");
        f["KNRRR"].Should().Be("001");
        f["KNRME"].Should().Be("KG");
        f["ZZEXCLUSIVE"].Should().BeNull();
        f["KNRMAT"].Should().BeNull();
    }

    [Fact]
    public void Vbn1_key_combination_1_requires_customer()
    {
        var r = ValidVbn1();
        r.Customer = null;

        Validate(r).Should().ContainSingle(x => x.MemberNames.Contains("Customer"));
    }

    [Fact]
    public void Vbn1_key_combination_2_requires_sales_office_and_customer_group_3()
    {
        var r = ValidVbn1();
        r.KeyCombination = "2";

        Validate(r).Should().ContainSingle(x => x.MemberNames.Contains("SalesOffice"));
    }

    [Fact]
    public void Vbn1_key_combination_must_be_1_or_2()
    {
        var r = ValidVbn1();
        r.KeyCombination = "3";

        Validate(r).Should().Contain(x => x.MemberNames.Contains("KeyCombination"));
    }

    [Fact]
    public void Va41_maps_validity_dates_and_rejects_reversed_range()
    {
        var r = new Va41Request
        {
            RequestId = "VA41-1", SalesOrganization = "G210", DistributionChannel = "10", Division = "11",
            SoldTo = "100166", ShipTo = "100166", ValidFrom = new DateOnly(2026, 11, 1), ValidTo = new DateOnly(2026, 12, 31),
            Material = "FG001", Quantity = 100, Unit = "KG", Plant = "1000"
        };

        var f = SapRequestMapper.ToVa41(r);
        f["AUART"].Should().Be("ZVCF");
        f["GUEBG"].Should().Be("01.11.2026");
        f["GUEEN"].Should().Be("31.12.2026");
        Validate(r).Should().BeEmpty();

        r.ValidTo = new DateOnly(2026, 10, 1);
        Validate(r).Should().ContainSingle(x => x.MemberNames.Contains("ValidTo"));
    }

    [Fact]
    public void Batch_input_vk_382_is_success_without_document_number()
    {
        var result = SapResultInterpreter.InterpretBatchInput(new[]
        {
            new SapReturnMessage("S", "VK", "382", "Condition records for Free goods saved")
        });

        result.Status.Should().Be(PostingStatus.Succeeded);
        result.Document!.DocumentNumber.Should().BeNull();
    }

    [Fact]
    public void Batch_input_without_382_is_failed_with_formatted_messages()
    {
        var result = SapResultInterpreter.InterpretBatchInput(new[]
        {
            new SapReturnMessage("E", "M3", "305", "Material FG999 does not exist"),
            new SapReturnMessage("E", "00", "344", "No batch input data for screen SAPMV13N 1980")
        });

        result.Status.Should().Be(PostingStatus.Failed);
        result.ErrorMessage.Should().Contain("Material FG999 does not exist");
    }

    [Fact]
    public void Batch_input_without_messages_is_unknown()
    {
        SapResultInterpreter.InterpretBatchInput(Array.Empty<SapReturnMessage>()).Status.Should().Be(PostingStatus.Unknown);
    }
}
