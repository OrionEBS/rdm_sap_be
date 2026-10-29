using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Rdm.Application.Requests;
using Rdm.Sap;

namespace Rdm.Tests;

public class AccDocumentAndContractTests
{
    internal static AccDocumentPostRequest ValidDocument() => new()
    {
        RequestId = "ZB-1",
        CompanyCode = "g210",
        DocumentType = "zb",
        DocumentDate = new DateOnly(2026, 10, 15),
        PostingDate = new DateOnly(2026, 10, 15),
        Reference = "RDM-QT-0001",
        HeaderText = "Quyết toán CK",
        Lines =
        {
            new AccDocumentLine
            {
                LineType = AccDocumentLineType.GL, DebitCredit = "S", Amount = 700_000m, GlAccount = "31140010",
                ProfitCenter = "g2101", Assignment = "CT-01",
                ProfitabilitySegment = new() { ["kndnr"] = "100166", ["ARTNR"] = "100016", ["VKORG"] = "g210" }
            },
            new AccDocumentLine { LineType = AccDocumentLineType.GL, DebitCredit = "S", Amount = 300_000m, GlAccount = "31130010" },
            new AccDocumentLine
            {
                LineType = AccDocumentLineType.Customer, DebitCredit = "h", Amount = 1_000_000m, Customer = "100166",
                CreditControlArea = "g110"
            }
        }
    };

    private static List<ValidationResult> Validate(object o)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(o, new ValidationContext(o), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Document_maps_sign_by_debit_credit_and_pads_numeric_codes()
    {
        var m = AccDocumentMapper.Map(ValidDocument());

        m.Header["BUS_ACT"].Should().Be("RFBU");
        m.Header["COMP_CODE"].Should().Be("G210");
        m.Header["DOC_TYPE"].Should().Be("ZB");
        m.Header["PSTNG_DATE"].Should().Be("20261015");
        m.Header["REF_DOC_NO"].Should().Be("RDM-QT-0001");
        m.Header["NEG_POSTNG"].Should().BeNull();

        m.AccountGl.Should().HaveCount(2);
        m.AccountGl[0]["ITEMNO_ACC"].Should().Be("0000000001");
        m.AccountGl[0]["GL_ACCOUNT"].Should().Be("0031140010");
        m.AccountGl[0]["PROFIT_CTR"].Should().Be("G2101");
        m.AccountReceivable.Single()["CUSTOMER"].Should().Be("0000100166");
        m.AccountReceivable.Single()["ITEMNO_ACC"].Should().Be("0000000003");
        m.AccountReceivable.Single()["C_CTR_AREA"].Should().Be("G110");

        m.CurrencyAmount.Select(a => a["AMT_DOCCUR"]).Should().Equal(700_000m, 300_000m, -1_000_000m);
        m.CurrencyAmount.Should().OnlyContain(a => (string)a["CURRENCY"]! == "VND" && a["AMT_BASE"] == null);

        m.Criteria.Should().HaveCount(3);
        m.Criteria.Single(c => (string)c["FIELDNAME"]! == "KNDNR")["CHARACTER"].Should().Be("0000100166");
        m.Criteria.Single(c => (string)c["FIELDNAME"]! == "ARTNR")["CHARACTER"].Should().Be("000000000000100016");
        m.Criteria.Single(c => (string)c["FIELDNAME"]! == "VKORG")["CHARACTER"].Should().Be("G210");
    }

    [Fact]
    public void Tax_line_carries_signed_base_amount()
    {
        var r = ValidDocument();
        r.Lines = new()
        {
            new AccDocumentLine { LineType = AccDocumentLineType.Customer, DebitCredit = "S", Amount = 1_080_000m, Customer = "100166" },
            new AccDocumentLine { LineType = AccDocumentLineType.GL, DebitCredit = "H", Amount = 1_000_000m, GlAccount = "32209010", TaxCode = "o8" },
            new AccDocumentLine { LineType = AccDocumentLineType.Tax, DebitCredit = "H", Amount = 80_000m, TaxCode = "o8", TaxBaseAmount = 1_000_000m }
        };

        var m = AccDocumentMapper.Map(r);

        m.AccountTax.Single()["TAX_CODE"].Should().Be("O8");
        m.AccountTax.Single()["ACCT_KEY"].Should().BeNull();
        m.CurrencyAmount[2]["AMT_DOCCUR"].Should().Be(-80_000m);
        m.CurrencyAmount[2]["AMT_BASE"].Should().Be(-1_000_000m);
        Validate(r).Should().BeEmpty();
    }

    [Fact]
    public void Unbalanced_document_is_rejected()
    {
        var r = ValidDocument();
        r.Lines[2].Amount = 999_999m;

        Validate(r).Should().ContainSingle(v => v.ErrorMessage!.Contains("không cân"));
    }

    [Fact]
    public void Line_rules_are_validated()
    {
        var r = ValidDocument();
        r.Lines[0].GlAccount = null;
        r.Lines[1].CreditControlArea = "G110";
        r.Lines.Add(new AccDocumentLine { LineType = AccDocumentLineType.Tax, DebitCredit = "S", Amount = 1m });
        r.Lines.Add(new AccDocumentLine { LineType = AccDocumentLineType.GL, DebitCredit = "H", Amount = 1m, GlAccount = "1" });

        var errors = Validate(r).Select(v => v.ErrorMessage).ToList();

        errors.Should().Contain(e => e!.StartsWith("Dòng 1:") && e.Contains("GlAccount"));
        errors.Should().Contain(e => e!.StartsWith("Dòng 2:") && e.Contains("chỉ dùng cho dòng Customer"));
        errors.Should().Contain(e => e!.StartsWith("Dòng 4:") && e.Contains("TaxCode"));
        errors.Should().Contain(e => e!.StartsWith("Dòng 4:") && e.Contains("TaxBaseAmount"));
    }

    [Fact]
    public void Valid_document_passes_validation_and_negative_posting_sets_flag()
    {
        var r = ValidDocument();
        r.NegativePosting = true;

        Validate(r).Should().BeEmpty();
        AccDocumentMapper.Map(r).Header["NEG_POSTNG"].Should().Be("X");
    }

    // ------------------------------------------------------------------ contracts

    internal static ContractCreateRequest ValidContract() => new()
    {
        RequestId = "HD-1",
        ContractType = "zvcq",
        SalesOrganization = "G210",
        DistributionChannel = "10",
        Division = "10",
        SoldTo = "100166",
        ShipTo = "100166",
        CustomerPoNumber = "HĐ 01/2026",
        ValidFrom = new DateOnly(2026, 11, 1),
        ValidTo = new DateOnly(2027, 10, 31),
        ShippingCondition = "pk",
        Items =
        {
            new ContractItemInput { Material = "100016", TargetQuantity = 1000m, Unit = "to", Plant = "g211" },
            new ContractItemInput { Material = "100017", TargetQuantity = 500m, Unit = "TO", Plant = "G211" }
        }
    };

    [Fact]
    public void Create_sets_approval_blocks_items_partners_and_contract_data()
    {
        var m = ContractMapper.MapCreate(ValidContract(), "30", "30");

        m.Header["DOC_TYPE"].Should().Be("ZVCQ");
        m.Header["CT_VALID_F"].Should().Be("20261101");
        m.Header["DLV_BLOCK"].Should().Be("30");
        m.Header["BILL_BLOCK"].Should().Be("30");
        m.HeaderX["UPDATEFLAG"].Should().Be("I");
        m.HeaderX["DLV_BLOCK"].Should().Be("X");
        m.HeaderX.Should().NotContainKey("SALES_OFF");

        m.Items.Select(i => i["ITM_NUMBER"]).Should().Equal("000010", "000020");
        m.Items[0]["MATERIAL"].Should().Be("000000000000100016");
        m.Items[0]["TARGET_QU"].Should().Be("TO");
        m.ItemsX[0]["UPDATEFLAG"].Should().Be("I");
        m.ItemsX[0]["ITM_NUMBER"].Should().Be("000010");
        m.ItemsX[0]["TARGET_QTY"].Should().Be("X");

        m.Partners.Select(p => (p["PARTN_ROLE"], p["PARTN_NUMB"]))
            .Should().Equal(("AG", "0000100166"), ("WE", "0000100166"));
        m.Data.Select(d => d["CON_EN_DAT"]).Should().Equal("20271031", "20271031");
        m.DataX.Should().OnlyContain(x => (string)x["UPDATEFLAG"]! == "I");
    }

    [Fact]
    public void Create_without_approval_has_no_blocks()
    {
        var r = ValidContract();
        r.RequireApproval = false;

        var m = ContractMapper.MapCreate(r, "30", "30");

        m.Header["DLV_BLOCK"].Should().BeNull();
        m.HeaderX.Should().NotContainKey("DLV_BLOCK");
    }

    private static ContractSnapshot Snapshot(string? dlv = "30", string? bill = "30") => new(
        "1100000100", "G", "ZVCQ", dlv, bill, new DateOnly(2026, 11, 1), new DateOnly(2027, 10, 31),
        new[]
        {
            new ContractSnapshotItem(10, "000000000000100016", null),
            new ContractSnapshotItem(20, "000000000000100017", "Z1")
        },
        new HashSet<int> { 0, 10, 20 });

    [Fact]
    public void Change_adds_updates_rejects_and_reapplies_approval_block()
    {
        var r = new ContractChangeRequest
        {
            RequestId = "HD-1-C1",
            ContractNumber = "1100000100",
            ValidTo = new DateOnly(2027, 12, 31),
            Items =
            {
                new ContractItemChange { Action = ContractItemAction.Update, ItemNumber = 10, TargetQuantity = 1200m },
                new ContractItemChange { Action = ContractItemAction.Add, Material = "100018", TargetQuantity = 50m, Unit = "TO", Plant = "G211" }
            }
        };

        var m = ContractMapper.MapChange(r, Snapshot(dlv: null, bill: null), "30", "30", out var error);

        error.Should().BeNull();
        m.HeaderX["UPDATEFLAG"].Should().Be("U");
        m.Header["CT_VALID_T"].Should().Be("20271231");
        m.Header["DLV_BLOCK"].Should().Be("30");
        m.HeaderX.Should().ContainKeys("CT_VALID_T", "DLV_BLOCK", "BILL_BLOCK").And.NotContainKey("CT_VALID_F");

        m.Items.Select(i => i["ITM_NUMBER"]).Should().Equal("000010", "000030");
        m.ItemsX[0]["UPDATEFLAG"].Should().Be("U");
        m.ItemsX[0].Should().ContainKey("TARGET_QTY").And.NotContainKey("MATERIAL");
        m.ItemsX[1]["UPDATEFLAG"].Should().Be("I");

        // dòng mới: contract data I với hiệu lực đến mới; VEDA cũ (0, 10, 20): U chỉ cờ CON_EN_DAT.
        m.Data.Select(d => d["ITM_NUMBER"]).Should().Equal("000030", "000000", "000010", "000020");
        m.Data[0]["CON_ST_DAT"].Should().Be("20261101");
        m.DataX.Skip(1).Should().OnlyContain(x => (string)x["UPDATEFLAG"]! == "U" && x.ContainsKey("CON_EN_DAT") && !x.ContainsKey("CON_ST_DAT"));
    }

    [Fact]
    public void Change_rejects_unknown_or_already_rejected_items()
    {
        var unknown = new ContractChangeRequest
        {
            ContractNumber = "1100000100",
            Items = { new ContractItemChange { Action = ContractItemAction.Reject, ItemNumber = 99, RejectionReason = "Z1" } }
        };
        ContractMapper.MapChange(unknown, Snapshot(), "30", "30", out var e1);
        e1.Should().Contain("không có dòng 99");

        var rejected = new ContractChangeRequest
        {
            ContractNumber = "1100000100",
            Items = { new ContractItemChange { Action = ContractItemAction.Update, ItemNumber = 20, TargetQuantity = 1m } }
        };
        ContractMapper.MapChange(rejected, Snapshot(), "30", "30", out var e2);
        e2.Should().Contain("đã bị từ chối");
    }

    [Fact]
    public void Change_reject_sets_reason_only()
    {
        var r = new ContractChangeRequest
        {
            ContractNumber = "1100000100",
            RequireReapproval = false,
            Items = { new ContractItemChange { Action = ContractItemAction.Reject, ItemNumber = 10, RejectionReason = "z1" } }
        };

        var m = ContractMapper.MapChange(r, Snapshot(), "30", "30", out var error);

        error.Should().BeNull();
        m.Items.Single()["REASON_REJ"].Should().Be("Z1");
        m.ItemsX.Single().Keys.Should().BeEquivalentTo("UPDATEFLAG", "ITM_NUMBER", "REASON_REJ");
        m.HeaderX.Keys.Should().BeEquivalentTo("UPDATEFLAG");
        m.Data.Should().BeEmpty();
    }

    [Fact]
    public void Approve_releases_only_approval_blocks()
    {
        var both = ContractMapper.MapApprove(Snapshot("30", "30"), "30", "30");
        both.Required.Should().BeTrue();
        both.Header.Should().BeEquivalentTo(new Dictionary<string, object?> { ["DLV_BLOCK"] = "", ["BILL_BLOCK"] = "" });
        both.HeaderX.Keys.Should().BeEquivalentTo("UPDATEFLAG", "DLV_BLOCK", "BILL_BLOCK");

        var credit = ContractMapper.MapApprove(Snapshot("Z1", "30"), "30", "30");
        credit.Header.Keys.Should().BeEquivalentTo("BILL_BLOCK");
        credit.Notes.Should().ContainSingle(n => n.Contains("Z1"));

        ContractMapper.MapApprove(Snapshot(null, null), "30", "30").Required.Should().BeFalse();
    }

    [Fact]
    public void Contract_validation_rules()
    {
        var bad = ValidContract();
        bad.ValidTo = new DateOnly(2026, 1, 1);
        bad.Items.Add(new ContractItemInput { Material = "X", TargetQuantity = 5m });
        var errors = Validate(bad).Select(v => v.ErrorMessage).ToList();
        errors.Should().Contain(e => e!.Contains("ValidTo"));
        errors.Should().Contain(e => e!.StartsWith("Dòng 3:"));

        Validate(new ContractChangeRequest { RequestId = "x", ContractNumber = "1" })
            .Should().Contain(v => v.ErrorMessage!.Contains("Không có thay đổi"));

        Validate(new ContractChangeRequest
        {
            RequestId = "x", ContractNumber = "1",
            Items = { new ContractItemChange { Action = ContractItemAction.Update, ItemNumber = 10, Material = "1" } }
        }).Should().Contain(v => v.ErrorMessage!.Contains("không đổi được Material"));
    }
}
