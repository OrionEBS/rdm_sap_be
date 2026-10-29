using FluentAssertions;
using Rdm.Api.Controllers;
using Rdm.Application.Postings;
using Rdm.Sap;

namespace Rdm.Tests;

public class PriceBapiTests
{
    private static PriceKey A990Key() => new("ZVLP", PriceConditionRules.A990, new Dictionary<string, string>
    {
        ["VKORG"] = "G210", ["VTWEG"] = "10", ["KUNAG"] = "0000100166", ["KUNWE"] = "0000200379",
        ["MATNR"] = "000000000000100016", ["VSBED"] = "PK", ["WERKS"] = "G112"
    });

    private static readonly PriceBapiItemValues Values = new(1_500_000m, "VND", 1, "TO", "C", "", Delete: false);

    [Fact]
    public void Create_maps_to_operation_009_with_temporary_number_and_varkey()
    {
        var key = A990Key();
        var t = PriceBapiMapper.Create(key, new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31), Values, "TUANNA", new DateOnly(2026, 10, 22));

        var ct = t.ConditionTables.Should().ContainSingle().Subject;
        ct["OPERATION"].Should().Be("009");
        ct["COND_NO"].Should().Be("$000000001");
        ct["TABLE_NO"].Should().Be("990");
        ct["COND_USAGE"].Should().Be("A");
        ct["APPLICATIO"].Should().Be("V");
        ct["VALID_FROM"].Should().Be("20261101");
        ct["VALID_TO"].Should().Be("20261231");
        // VARKEY = VKORG(4) VTWEG(2) KUNAG(10) KUNWE(10) MATNR(18) VSBED(2) WERKS(4) + KFRST(1) — giống KONH-VAKEY trên DRF-930
        ((string)ct["VARKEY"]!).Should().Be("G21010" + "0000100166" + "0000200379" + "000000000000100016" + "PK" + "G112" + " ");

        var hd = t.Headers.Single();
        hd["CREATED_BY"].Should().Be("TUANNA");
        hd["CREAT_DATE"].Should().Be("20261022");

        var it = t.Items.Single();
        it["COND_VALUE"].Should().Be(1_500_000m);
        it["CONDCURR"].Should().Be("VND");
        it["COND_UNIT"].Should().Be("TO");
        it["CALCTYPCON"].Should().Be("C");
        it["STATUS"].Should().Be("");
        it["INDIDELETE"].Should().Be("");
    }

    [Fact]
    public void Pending_release_status_is_the_last_varkey_character()
    {
        var t = PriceBapiMapper.Create(A990Key(), new DateOnly(2027, 1, 1), new DateOnly(2027, 3, 31),
            Values with { ReleaseStatus = "A" }, "TUANNA", new DateOnly(2026, 10, 22));

        ((string)t.ConditionTables.Single()["VARKEY"]!).Should().HaveLength(51).And.EndWith("G112A");
        ((string)t.Headers.Single()["VARKEY"]!).Should().EndWith("G112A");
    }

    [Fact]
    public void Status_change_keeps_every_period_and_puts_new_release_status_in_varkey()
    {
        var periods = new[]
        {
            (new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30), new DateOnly(2026, 6, 30)),
            (new DateOnly(2026, 8, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31))
        };
        var t = PriceBapiMapper.Change("0000119650", A990Key(), periods, Values with { ReleaseStatus = "A", Delete = true });

        t.ConditionTables.Should().HaveCount(2).And.OnlyContain(r =>
            (string)r["OPERATION"]! == "004" && (string)r["COND_NO"]! == "0000119650" && ((string)r["VARKEY"]!).EndsWith("A"));
        t.PhysicalDeletion.Should().BeFalse();
        t.Headers.Single()["VALID_FROM"].Should().Be("20260101");
        t.Headers.Single()["VALID_TO"].Should().Be("20261231");
        t.Items.Single()["INDIDELETE"].Should().Be("X");
    }

    [Fact]
    public void Validity_change_sends_new_period_before_physical_delete_of_old_period()
    {
        // Kiểm chứng trên DRF-930: 004 kỳ mới TRƯỚC rồi 003 kỳ cũ (+ PI_PHYSICAL_DELETION) → đúng 1 dòng Axxx, không có cờ xóa KONP.
        var periods = new[] { (new DateOnly(2027, 1, 1), new DateOnly(2027, 5, 31), new DateOnly(2027, 6, 30)) };
        var t = PriceBapiMapper.Change("0000119651", A990Key(), periods, Values);

        t.PhysicalDeletion.Should().BeTrue();
        t.ConditionTables.Select(r => ((string)r["OPERATION"]!, (string)r["VALID_TO"]!))
            .Should().Equal(("004", "20270630"), ("003", "20270531"));
        t.Headers.Single()["VALID_TO"].Should().Be("20270630");
        t.Items.Single()["INDIDELETE"].Should().Be("");
    }

    [Fact]
    public void Batch_renumbers_only_temporary_condition_numbers()
    {
        // Gom nhiều bảng giá tạo mới vào 1 lần gọi: mỗi bảng giá 1 số tạm riêng, map lại qua TO_BAPIKNUMHS.
        var t = PriceBapiMapper.WithTemporaryNumber(
            PriceBapiMapper.Create(A990Key(), new DateOnly(2032, 1, 1), new DateOnly(2032, 3, 31), Values, "TUANNA", new DateOnly(2026, 10, 23)),
            "$000000007");

        t.ConditionTables.Concat(t.Headers).Concat(t.Items).Should().OnlyContain(r => (string)r["COND_NO"]! == "$000000007");

        var change = PriceBapiMapper.WithTemporaryNumber(
            PriceBapiMapper.Change("0000119650", A990Key(), new[] { (new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30), new DateOnly(2026, 6, 30)) }, Values),
            "$000000008");
        change.ConditionTables.Single()["COND_NO"].Should().Be("0000119650");   // KNUMH thật giữ nguyên
    }

    [Fact]
    public void Within_request_check_groups_by_key_and_reports_later_line()
    {
        var other = A990Key() with { Values = new Dictionary<string, string>(A990Key().Values) { ["KUNAG"] = "0000100001" } };
        var lines = new List<(int, PriceKey, DateOnly, DateOnly)>
        {
            (1, A990Key(), new DateOnly(2032, 1, 1), new DateOnly(2032, 3, 31)),
            (2, other, new DateOnly(2032, 1, 1), new DateOnly(2032, 3, 31)),       // khóa khác — không trùng
            (3, A990Key(), new DateOnly(2032, 4, 1), new DateOnly(2032, 6, 30)),   // cùng khóa, liền kề — không trùng
            (4, A990Key(), new DateOnly(2032, 3, 15), new DateOnly(2032, 4, 15))   // chồng dòng 1 (và 3)
        };

        var errors = PriceConditionRules.CheckWithinRequest(lines);

        errors.Should().ContainSingle();
        errors[4].Should().Contain("dòng 1");
    }

    private static PostingResponse Posting(PostingStatus status, params SapReturnMessage[] messages) => new()
    {
        RequestId = "R1", Status = status, Messages = messages, ErrorCode = status == PostingStatus.Succeeded ? null : PriceErrorCodes.Partial
    };

    [Fact]
    public void Result_counts_success_failure_and_exposes_sap_error_code()
    {
        var p = Posting(PostingStatus.Failed,
            new SapReturnMessage("S", "RDM", PriceErrorCodes.Ok, "Tạo bảng giá 0000200001", MessageV1: "0000200001", Item: "1"),
            new SapReturnMessage("E", "VK", "021", "Condition record exists", Item: "2"),
            new SapReturnMessage("E", "RDM", PriceErrorCodes.SapError, "VK/021: Condition record exists", MessageV2: "VK/021", Item: "2"));

        var r = PriceBapiResult.From(p);

        r.SuccessCount.Should().Be(1);
        r.FailedCount.Should().Be(1);
        r.Records[0].ConditionRecordNumber.Should().Be("0000200001");
        r.Records[0].Success.Should().BeTrue();
        var failed = r.Records[1];
        failed.Success.Should().BeFalse();
        failed.ErrorCode.Should().Be(PriceErrorCodes.SapError);
        failed.SapErrorCode.Should().Be("VK/021");
        failed.Message.Should().Be("Condition record exists");
        failed.SapMessages.Should().ContainSingle();
    }

    [Fact]
    public void Validation_error_reports_rdm_code_without_sap_code()
    {
        var p = Posting(PostingStatus.Failed,
            new SapReturnMessage("E", "RDM", PriceErrorCodes.Overlap, "Chồng hiệu lực với bảng giá đã duyệt 0000119650", Item: "1"));

        var record = PriceBapiResult.From(p).Records.Single();

        record.ErrorCode.Should().Be(PriceErrorCodes.Overlap);
        record.SapErrorCode.Should().BeNull();
        record.Message.Should().Contain("Chồng hiệu lực");
    }

    [Fact]
    public void Already_in_target_state_counts_as_skipped()
    {
        var p = Posting(PostingStatus.Succeeded,
            new SapReturnMessage("I", "RDM", PriceErrorCodes.AlreadyApproved, "đã duyệt", MessageV1: "0000119650", Item: "0000119650"),
            new SapReturnMessage("S", "RDM", PriceErrorCodes.Ok, "đã duyệt", MessageV1: "0000119651", Item: "0000119651"));

        var r = PriceBapiResult.From(p);

        r.SuccessCount.Should().Be(1);
        r.SkippedCount.Should().Be(1);
        r.FailedCount.Should().Be(0);
    }
}
