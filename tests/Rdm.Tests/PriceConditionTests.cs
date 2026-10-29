using FluentAssertions;
using Rdm.Application.Postings;
using Rdm.Application.Requests;
using Rdm.Sap;

namespace Rdm.Tests;

public class PriceConditionTests
{
    private static PriceCreateItem Zvlp(DateOnly from, DateOnly? to = null, decimal amount = 1_250_000m) => new()
    {
        ConditionType = "zvlp", SalesOrganization = "G210", DistributionChannel = "10", SoldTo = "100002", ShipTo = "100002",
        Material = "100016", ShippingCondition = "pk", Plant = "g130", Amount = amount, Unit = "TO", ValidFrom = from, ValidTo = to
    };

    private static PriceKey Key(PriceKeyInput input) =>
        PriceConditionRules.BuildKey(input, allowPartial: false, out var e) ?? throw new InvalidOperationException(e!.Value.Message);

    private static PriceRecord Existing(PriceKey key, string knumh, DateOnly from, DateOnly to, string kfrst = "", bool deleted = false) =>
        new(knumh, key, kfrst, "01", from, to, deleted, 1_000_000m, "VND", 1, "TO");

    [Fact]
    public void A990_key_is_padded_into_varkey_like_konh()
    {
        var key = Key(Zvlp(new DateOnly(2026, 11, 1)));

        key.Table.Number.Should().Be("990");
        key.ConditionType.Should().Be("ZVLP");
        key.VarKey.Should().Be("G210100000100002000010000200000000000010001" + "6PKG130");
        key.VarKey.Length.Should().Be(50);
    }

    [Fact]
    public void Varkey_round_trips_through_konh_format()
    {
        // VAKEY thật của KNUMH 0000116900 trên DRF-930.
        var key = PriceConditionRules.KeyFromVarKey("ZVLP", PriceConditionRules.A990,
            "G2101000001000010000100001000000000000760000PKG112" + new string(' ', 50));

        key.Values["KUNAG"].Should().Be("0000100001");
        key.Values["MATNR"].Should().Be("000000000000760000");
        key.Values["WERKS"].Should().Be("G112");
        key.VarKey.Should().Be("G2101000001000010000100001000000000000760000PKG112");
    }

    [Fact]
    public void Zmin_table_follows_incoterms_or_region_and_zviv_uses_order_sales_org()
    {
        var a912 = Key(new PriceKeyInput { ConditionType = "ZMIN", SalesOrganization = "G210", DistributionChannel = "10", Material = "100089", Incoterms = "EXW" });
        a912.Table.Number.Should().Be("912");
        a912.VarKey.Should().Be("G21010000000000000100089EXW"); // = VAKEY thật trên DRF (trừ KFRST)

        // Region: chỉ để đọc A919; tạo / sửa ZMIN chỉ theo A912 (VK11 nghiệp vụ không dùng Region).
        var region = new PriceKeyInput { ConditionType = "ZMIN", SalesOrganization = "G210", DistributionChannel = "10", Material = "1", Region = "79" };
        PriceConditionRules.BuildKey(region, allowPartial: true, out _)!.Table.Number.Should().Be("919");
        PriceConditionRules.BuildKey(region, allowPartial: false, out var regionError).Should().BeNull();
        regionError!.Value.Code.Should().Be(PriceErrorCodes.InvalidKey);

        var zviv = Key(new PriceKeyInput { ConditionType = "ZVIV", SalesOrganization = "G210", Plant = "G112", Material = "100016" });
        zviv.Table.Number.Should().Be("991");
        zviv.VarKey.Should().Be("G210G112000000000000100016"); // = VAKEY thật trên DRF
    }

    [Theory]
    [InlineData("ZXXX", PriceErrorCodes.InvalidConditionType)]
    [InlineData("ZMIN", PriceErrorCodes.InvalidKey)]
    public void Invalid_type_or_missing_zmin_dimension_is_rejected(string type, string code)
    {
        PriceConditionRules.BuildKey(new PriceKeyInput { ConditionType = type, SalesOrganization = "G210", DistributionChannel = "10", Material = "1" },
            allowPartial: false, out var error).Should().BeNull();
        error!.Value.Code.Should().Be(code);
    }

    [Fact]
    public void Missing_and_foreign_key_fields_are_rejected()
    {
        var missing = Zvlp(new DateOnly(2026, 11, 1));
        missing.Plant = null;
        PriceConditionRules.BuildKey(missing, false, out var e1).Should().BeNull();
        e1!.Value.Message.Should().Contain("WERKS");

        var foreign = Zvlp(new DateOnly(2026, 11, 1));
        foreign.Incoterms = "EXW";
        PriceConditionRules.BuildKey(foreign, false, out var e2).Should().BeNull();
        e2!.Value.Message.Should().Contain("INCO1");
    }

    [Fact]
    public void Overlap_rules_by_status_and_mode()
    {
        var key = Key(Zvlp(new DateOnly(2026, 11, 1)));
        var existing = new[]
        {
            Existing(key, "0000000001", new DateOnly(2025, 1, 1), new DateOnly(2029, 12, 31)),            // đã duyệt, bao trùm
            Existing(key, "0000000002", new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 31), kfrst: "A"),  // chờ duyệt
            Existing(key, "0000000003", new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31), deleted: true)
        };

        var reject = PriceConditionRules.CheckAgainstExisting(key, new DateOnly(2026, 11, 1), new DateOnly(2027, 12, 31), existing, PriceOverlapMode.Reject);
        reject.Select(r => (r.Type, r.Code)).Should().BeEquivalentTo(new[]
        {
            ("E", PriceErrorCodes.Overlap), ("E", PriceErrorCodes.OverlapPending)
        });

        var replace = PriceConditionRules.CheckAgainstExisting(key, new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31), existing, PriceOverlapMode.Replace);
        replace.Should().ContainSingle().Which.Should().Be(("W", PriceErrorCodes.WillAdjustExisting,
            "Bảng giá 0000000001 (01.01.2025–31.12.2029) sẽ bị SAP tách thành 2 kỳ quanh kỳ mới."));

        var same = PriceConditionRules.CheckAgainstExisting(key, new DateOnly(2025, 1, 1), new DateOnly(2029, 12, 31),
            existing.Take(1), PriceOverlapMode.Replace);
        same.Single().Code.Should().Be(PriceErrorCodes.SamePeriodExists);

        // Khóa khác (plant khác) không bị tính; bản đang sửa (ignoreRecord) không bị tính.
        var other = Key(new PriceCreateItem
        {
            ConditionType = "ZVLP", SalesOrganization = "G210", DistributionChannel = "10", SoldTo = "100002", ShipTo = "100002",
            Material = "100016", ShippingCondition = "PK", Plant = "G114", Unit = "TO", ValidFrom = new DateOnly(2026, 1, 1)
        });
        PriceConditionRules.CheckAgainstExisting(other, new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31), existing, PriceOverlapMode.Reject)
            .Should().BeEmpty();
        PriceConditionRules.CheckAgainstExisting(key, new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31), existing.Take(1), PriceOverlapMode.Reject,
            ignoreRecord: "0000000001").Should().BeEmpty();
    }

    [Fact]
    public void Duplicates_within_request_are_flagged_on_later_line()
    {
        var key = Key(Zvlp(new DateOnly(2026, 11, 1)));
        var errors = PriceConditionRules.CheckWithinRequest(new[]
        {
            (1, key, new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31)),
            (2, key, new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31)),
            (3, key, new DateOnly(2026, 12, 1), new DateOnly(2027, 1, 15))
        });

        errors.Keys.Should().Equal(3);
        errors[3].Should().Contain("dòng 1");
    }

    [Theory]
    [InlineData(1_080_000, 10, 8, 1, 1_100_000)]      // giá gồm VAT 8% → 10%
    [InlineData(1_000_000, 10, null, 1, 1_100_000)]   // giá chưa VAT → gồm VAT 10%
    [InlineData(1_234_567, 10, 8, 1000, 1_257_000)]   // 1 257 429.6 làm tròn nghìn
    [InlineData(-20_000, 10, 8, 1, -20_370)]          // ZVDI âm giữ dấu: -20000/1.08*1.1 = -20370.37
    public void Vat_conversion(decimal oldAmount, decimal vat, int? oldVat, decimal rounding, decimal expected) =>
        PriceConditionRules.ApplyVat(oldAmount, vat, oldVat is null ? null : (decimal)oldVat, rounding).Should().Be(expected);

    [Fact]
    public void Amount_sign_rules()
    {
        PriceConditionRules.AmountSignValid("ZVDI", -1).Should().BeTrue();
        PriceConditionRules.AmountSignValid("ZVDI", 1).Should().BeFalse();
        PriceConditionRules.AmountSignValid("ZVLP", 1).Should().BeTrue();
        PriceConditionRules.AmountSignValid("ZMIN", 0).Should().BeFalse();
    }

    [Fact]
    public void Upload_rows_map_to_zssd_price_upld()
    {
        var item = Zvlp(new DateOnly(2026, 11, 1));
        var line = new PriceCreateLine(3, Key(item), item, new DateOnly(2026, 11, 1), PriceConditionRules.MaxDate);

        var row = PriceConditionRules.ToUploadRow(line);

        row["ROW_NO"].Should().Be(3);
        row["COND_TABLE"].Should().Be("A990");
        row["KUNAG"].Should().Be("0000100002");
        row["MATNR"].Should().Be("000000000000100016");
        row["WERKS"].Should().Be("G130");
        row["VKORGAU"].Should().BeNull();
        row["KBETR"].Should().Be(1_250_000m);
        row["KONWA"].Should().Be("VND");
        row["KMEIN"].Should().Be("TO");
        row["DATAB"].Should().Be("20261101");
        row["DATBI"].Should().Be("99991231");

        var zviv = new PriceCreateItem { ConditionType = "ZVIV", SalesOrganization = "G210", Plant = "G112", Material = "100016", Amount = 1m, Unit = "TO", ValidFrom = new DateOnly(2027, 1, 1) };
        var zivRow = PriceConditionRules.ToUploadRow(new PriceCreateLine(1, Key(zviv), zviv, new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31)));
        zivRow["COND_TABLE"].Should().Be("A991");
        zivRow["VKORGAU"].Should().Be("G210");
        zivRow["VKORG"].Should().BeNull();
    }

    [Fact]
    public void Change_and_release_rows_carry_knumh_and_only_changed_fields()
    {
        var change = PriceConditionRules.ToChangeRow(2, "0000116900", 1_350_000m, null, null, new DateOnly(2028, 12, 31), new DateOnly(2026, 3, 1));
        change["KNUMH"].Should().Be("0000116900");
        change["KBETR"].Should().Be(1_350_000m);
        change["KPEIN"].Should().BeNull();
        change["DATBI"].Should().Be("20281231");
        change["DATAB"].Should().Be("20260301");

        var release = PriceConditionRules.ToReleaseRow(1, "0000108730");
        release.Keys.Should().BeEquivalentTo("ROW_NO", "KNUMH");
    }
}
