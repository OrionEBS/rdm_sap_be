using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Rdm.Application.Postings;
using Rdm.Application.Requests;
using Rdm.Sap;

namespace Rdm.Tests;

public class MaterialTaxClassTests
{
    // RETURN của BAPI_MATERIAL_SAVEDATA luôn là S MM/356 (DRF-930), kể cả khi lỗi chi tiết nằm ở RETURNMESSAGES.
    private static readonly SapReturnMessage Mm356 = new("S", "MM", "356", "The material 100016 has been created or extended");
    private static readonly SapReturnMessage Mk103 = new("H", "MK", "103", "Trying to change: 100016");

    [Fact]
    public void M3_801_means_changed()
    {
        var o = SapResultInterpreter.InterpretMaterialSave(Mm356,
            new[] { Mk103, new SapReturnMessage("S", "M3", "801", "Material 100016 changed") });

        o.Ok.Should().BeTrue();
        o.Changed.Should().BeTrue();
        o.KeyMessage!.Number.Should().Be("801");
    }

    [Fact]
    public void M3_810_means_success_without_change()
    {
        var o = SapResultInterpreter.InterpretMaterialSave(Mm356,
            new[] { Mk103, new SapReturnMessage("S", "M3", "810", "No changes made") });

        o.Ok.Should().BeTrue();
        o.Changed.Should().BeFalse();
    }

    [Fact]
    public void Error_in_details_wins_over_success_return()
    {
        var o = SapResultInterpreter.InterpretMaterialSave(Mm356,
            new[] { Mk103, new SapReturnMessage("E", "M3", "014", "Tax category MWS1 does not define classification 9") });

        o.Ok.Should().BeFalse();
        o.KeyMessage!.Message.Should().Contain("classification 9");
    }

    [Fact]
    public void Error_return_is_failure()
    {
        SapResultInterpreter.InterpretMaterialSave(new SapReturnMessage("E", "M3", "098", "Enter a material type"),
            Array.Empty<SapReturnMessage>()).Ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("100016", "000000000000100016")]
    [InlineData(" 000000000000100016 ", "000000000000100016")]
    [InlineData("301423r2", "301423R2")]
    public void Material_number_is_converted_to_internal_format(string input, string expected) =>
        SapClient.InternalMaterialNumber(input).Should().Be(expected);

    private static List<ValidationResult> Validate(object o)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(o, new ValidationContext(o), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Common_tax_class_covers_items_without_own_value()
    {
        var r = new MaterialTaxClassRequest
        {
            RequestId = "T1", TaxClass = "5",
            Items = { new MaterialTaxClassItem { Material = "100016" }, new MaterialTaxClassItem { Material = "100017", TaxClass = "1" } }
        };
        Validate(r).Should().BeEmpty();

        r.TaxClass = null;
        Validate(r).Should().ContainSingle(x => x.MemberNames.Contains("TaxClass"));
    }

    [Fact]
    public void Duplicate_materials_are_rejected_even_with_leading_zeros()
    {
        var r = new MaterialTaxClassRequest
        {
            RequestId = "T1", TaxClass = "5",
            Items = { new MaterialTaxClassItem { Material = "100016" }, new MaterialTaxClassItem { Material = "000000000000100016" } }
        };
        Validate(r).Should().ContainSingle(x => x.ErrorMessage!.Contains("lặp"));
    }
}
