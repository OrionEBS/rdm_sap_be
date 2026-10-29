using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Rdm.Application.Requests;
using Rdm.Sap;

namespace Rdm.Tests;

public class SapRequestMapperTests
{
    internal static Fb01Request ValidFb01(string requestId = "REQ-1") => new()
    {
        RequestId = requestId,
        CompanyCode = "vn10",
        DocumentType = "da",
        DocumentDate = new DateOnly(2026, 10, 5),
        PostingDate = new DateOnly(2026, 10, 6),
        Reference = "INV-001",
        HeaderText = "Thu tiền",
        GlLine = new Fb01GlLine { PostingKey = "40", GlAccount = "11210010", Amount = 1_500_000m, CostCenter = "g21010103" },
        CustomerLine = new Fb01CustomerLine { PostingKey = "9", Customer = "10000123", Amount = 1_500_000m, SpecialGlIndicator = "a" }
    };

    [Fact]
    public void Fb01_maps_dates_as_dd_MM_yyyy_and_uppercases_codes()
    {
        var f = SapRequestMapper.ToFb01(ValidFb01());

        f["BLDAT"].Should().Be("05.10.2026");
        f["BUDAT"].Should().Be("06.10.2026");
        f["BUKRS"].Should().Be("VN10");
        f["BLART"].Should().Be("DA");
        f["ZZKOSTL_1"].Should().Be("G21010103");
        f["ZZUMSKZ_2"].Should().Be("A");
        f["BKTXT"].Should().Be("Thu tiền");
        f["ZZWRBTR_1"].Should().Be(1_500_000m);
    }

    [Fact]
    public void Posting_key_is_left_padded_to_two_digits()
    {
        SapRequestMapper.ToFb01(ValidFb01())["ZZBSCHL_2"].Should().Be("09");
    }

    [Fact]
    public void Blank_optional_fields_are_null_so_they_are_not_sent()
    {
        var r = ValidFb01();
        r.Reference = "  ";
        r.CreditControlArea = null;

        var f = SapRequestMapper.ToFb01(r);

        f["XBLNR"].Should().BeNull();
        f["KKBER"].Should().BeNull();
    }

    [Fact]
    public void Fb75_calculate_tax_flag_maps_to_X()
    {
        var r = new Fb75Request { CalculateTax = true, CompanyCode = "VN10", Customer = "1", DocumentType = "DG", GlAccount = "5", Amount = 1 };
        SapRequestMapper.ToFb75(r)["XMWST"].Should().Be("X");

        r.CalculateTax = false;
        SapRequestMapper.ToFb75(r)["XMWST"].Should().BeNull();
    }

    [Fact]
    public void Va01_maps_billing_date_and_quantity()
    {
        var r = new Va01Request
        {
            SalesOrganization = "vn10", DistributionChannel = "10", Division = "00", SoldTo = "100", ShipTo = "100",
            BillingDate = new DateOnly(2026, 1, 31), Material = "fg001", Quantity = 2.5m, Unit = "kg", Plant = "1000", Rate = 10_000m
        };

        var f = SapRequestMapper.ToVa01(r);

        f["AUART"].Should().Be("ZVCR");
        f["FKDAT"].Should().Be("31.01.2026");
        f["MATNR"].Should().Be("FG001");
        f["ZIEME"].Should().Be("KG");
        f["ZMENG"].Should().Be(2.5m);
    }

    [Fact]
    public void Field_lengths_follow_sap_structure()
    {
        var r = ValidFb01();
        r.CompanyCode = "TOO-LONG";
        r.HeaderText = new string('x', 26);

        var results = new List<ValidationResult>();
        Validator.TryValidateObject(r, new ValidationContext(r), results, validateAllProperties: true).Should().BeFalse();

        results.SelectMany(x => x.MemberNames).Should().Contain(new[] { "CompanyCode", "HeaderText" });
    }

    [Fact]
    public void Reversal_requires_object_type_and_key_together()
    {
        var r = new AccDocumentReversalRequest
        {
            RequestId = "R", CompanyCode = "VN10", DocumentNumber = "100000001", FiscalYear = "2026", ReasonCode = "01",
            ObjectType = "BKPFF"
        };

        var results = new List<ValidationResult>();
        Validator.TryValidateObject(r, new ValidationContext(r), results, validateAllProperties: true).Should().BeFalse();
        results.Should().ContainSingle(x => x.MemberNames.Contains("ObjectKey"));
    }
}
