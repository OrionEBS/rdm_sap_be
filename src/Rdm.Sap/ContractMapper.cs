using Rdm.Application.Requests;

namespace Rdm.Sap;

using Row = IReadOnlyDictionary<string, object?>;

/// <summary>Giá trị tham số BAPI hợp đồng — dictionary thuần để unit-test không cần SAP.</summary>
public sealed record ContractBapiInput(
    Row Header,
    Row HeaderX,
    IReadOnlyList<Row> Items,
    IReadOnlyList<Row> ItemsX,
    IReadOnlyList<Row> Partners,
    IReadOnlyList<Row> Data,
    IReadOnlyList<Row> DataX);

/// <summary>Hiện trạng hợp đồng đọc từ VBAK / VBAP / VEDA trước khi sửa / duyệt.</summary>
public sealed record ContractSnapshot(
    string ContractNumber,
    string DocumentCategory,
    string ContractType,
    string? DeliveryBlock,
    string? BillingBlock,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    IReadOnlyList<ContractSnapshotItem> Items,
    IReadOnlySet<int> ContractDataItems);

public sealed record ContractSnapshotItem(int ItemNumber, string Material, string? RejectionReason);

public sealed record ContractApprovePlan(bool Required, Row Header, Row HeaderX, IReadOnlyList<string> Notes);

/// <summary>
/// Map request hợp đồng → BAPI_CONTRACT_CREATEFROMDATA / BAPI_CUSTOMERCONTRACT_CHANGE.
/// Cách map theo Z_ESD013_CREATE_VA41 (F_CREATE_VA41, LZISD_FG_RDMF01 trên DRF-930) đã chạy ổn:
/// header + item + contract data (VEDA) theo từng dòng, INX với UPDATEFLAG, partner AG/WE, INT_NUMBER_ASSIGNMENT.
/// </summary>
public static class ContractMapper
{
    public const string Insert = "I";
    public const string Update = "U";

    public static ContractBapiInput MapCreate(ContractCreateRequest r, string? approvalDeliveryBlock, string? approvalBillingBlock)
    {
        var header = new Dictionary<string, object?>
        {
            ["DOC_TYPE"] = SapRequestMapper.Code(r.ContractType),
            ["SALES_ORG"] = SapRequestMapper.Code(r.SalesOrganization),
            ["DISTR_CHAN"] = SapRequestMapper.Code(r.DistributionChannel),
            ["DIVISION"] = SapRequestMapper.Code(r.Division),
            ["SALES_OFF"] = SapRequestMapper.Code(r.SalesOffice),
            ["SALES_GRP"] = SapRequestMapper.Code(r.SalesGroup),
            ["PURCH_NO_C"] = SapRequestMapper.Text(r.CustomerPoNumber),
            ["PURCH_DATE"] = SapRequestMapper.Dats(r.CustomerPoDate),
            ["CT_VALID_F"] = SapRequestMapper.Dats(r.ValidFrom),
            ["CT_VALID_T"] = SapRequestMapper.Dats(r.ValidTo),
            ["SHIP_COND"] = SapRequestMapper.Code(r.ShippingCondition),
            ["PMNTTRMS"] = SapRequestMapper.Code(r.PaymentTerms),
            ["DLV_BLOCK"] = r.RequireApproval ? SapRequestMapper.Code(approvalDeliveryBlock) : null,
            ["BILL_BLOCK"] = r.RequireApproval ? SapRequestMapper.Code(approvalBillingBlock) : null
        };

        var items = new List<Row>();
        var itemsX = new List<Row>();
        var data = new List<Row>();
        var dataX = new List<Row>();
        var next = 10;
        foreach (var it in r.Items)
        {
            var number = it.ItemNumber ?? next;
            next = Math.Max(next, number) + 10;
            AddItem(items, itemsX, number, Insert, it.Material, it.TargetQuantity, it.Unit, it.Plant, it.ShippingPoint, it.ShortText, null);
            AddData(data, dataX, number, Insert, r.ValidFrom, r.ValidTo);
        }

        var partners = new List<Row> { Partner("AG", r.SoldTo) };
        if (!string.IsNullOrWhiteSpace(r.ShipTo)) partners.Add(Partner("WE", r.ShipTo));
        if (!string.IsNullOrWhiteSpace(r.BillTo)) partners.Add(Partner("RE", r.BillTo));
        if (!string.IsNullOrWhiteSpace(r.Payer)) partners.Add(Partner("RG", r.Payer));

        return new ContractBapiInput(header, FlagsOf(header, Insert), items, itemsX, partners, data, dataX);
    }

    /// <summary>
    /// Map yêu cầu sửa trên hiện trạng hợp đồng. Trả lỗi (không ném) nếu yêu cầu không áp được vào hợp đồng.
    /// </summary>
    public static ContractBapiInput MapChange(
        ContractChangeRequest r, ContractSnapshot current, string? approvalDeliveryBlock, string? approvalBillingBlock,
        out string? error)
    {
        error = null;
        var header = new Dictionary<string, object?>
        {
            ["PURCH_NO_C"] = SapRequestMapper.Text(r.CustomerPoNumber),
            ["PURCH_DATE"] = SapRequestMapper.Dats(r.CustomerPoDate),
            ["CT_VALID_F"] = SapRequestMapper.Dats(r.ValidFrom),
            ["CT_VALID_T"] = SapRequestMapper.Dats(r.ValidTo),
            ["SHIP_COND"] = SapRequestMapper.Code(r.ShippingCondition),
            ["PMNTTRMS"] = SapRequestMapper.Code(r.PaymentTerms)
        };
        if (r.RequireReapproval)
        {
            header["DLV_BLOCK"] = SapRequestMapper.Code(approvalDeliveryBlock);
            header["BILL_BLOCK"] = SapRequestMapper.Code(approvalBillingBlock);
        }

        var existing = current.Items.ToDictionary(i => i.ItemNumber);
        var items = new List<Row>();
        var itemsX = new List<Row>();
        var data = new List<Row>();
        var dataX = new List<Row>();
        var next = (current.Items.Count == 0 ? 0 : current.Items.Max(i => i.ItemNumber)) + 10;
        var newNumbers = new HashSet<int>();

        foreach (var it in r.Items)
        {
            switch (it.Action)
            {
                case ContractItemAction.Add:
                {
                    var number = it.ItemNumber ?? next;
                    if (existing.ContainsKey(number) || !newNumbers.Add(number))
                    {
                        error = $"Dòng {number} đã tồn tại trên hợp đồng {current.ContractNumber}.";
                        return Empty;
                    }
                    next = Math.Max(next, number) + 10;
                    AddItem(items, itemsX, number, Insert, it.Material, it.TargetQuantity, it.Unit, it.Plant, it.ShippingPoint, it.ShortText, null);
                    AddData(data, dataX, number, Insert, r.ValidFrom ?? current.ValidFrom, r.ValidTo ?? current.ValidTo);
                    break;
                }
                case ContractItemAction.Update:
                case ContractItemAction.Reject:
                {
                    var number = it.ItemNumber!.Value;
                    if (!existing.TryGetValue(number, out var old))
                    {
                        error = $"Hợp đồng {current.ContractNumber} không có dòng {number}.";
                        return Empty;
                    }
                    if (old.RejectionReason is not null)
                    {
                        error = $"Dòng {number} đã bị từ chối (lý do {old.RejectionReason}) — không sửa được.";
                        return Empty;
                    }
                    if (it.Action == ContractItemAction.Update)
                        AddItem(items, itemsX, number, Update, null, it.TargetQuantity, it.Unit, it.Plant, it.ShippingPoint, it.ShortText, null);
                    else
                        AddItem(items, itemsX, number, Update, null, null, null, null, null, null, it.RejectionReason);
                    break;
                }
            }
        }

        // Đổi hiệu lực: cập nhật mọi bản ghi VEDA đang có (header 0 + từng dòng), trừ dòng mới thêm (đã có bản ghi I).
        if (r.ValidFrom is not null || r.ValidTo is not null)
            foreach (var number in current.ContractDataItems.Where(n => !newNumbers.Contains(n)).OrderBy(n => n))
                AddData(data, dataX, number, Update, r.ValidFrom, r.ValidTo);

        return new ContractBapiInput(header, FlagsOf(header, Update), items, itemsX, Array.Empty<Row>(), data, dataX);
    }

    /// <summary>Gỡ block duyệt: chỉ gỡ khi block hiện tại đúng mã duyệt; block khác giữ nguyên và ghi chú lại.</summary>
    public static ContractApprovePlan MapApprove(ContractSnapshot current, string? approvalDeliveryBlock, string? approvalBillingBlock)
    {
        var header = new Dictionary<string, object?>();
        var headerX = new Dictionary<string, object?> { ["UPDATEFLAG"] = Update };
        var notes = new List<string>();

        void Release(string field, string label, string? currentBlock, string? approvalBlock)
        {
            var approval = SapRequestMapper.Code(approvalBlock);
            if (currentBlock is null) return;
            if (approval is not null && currentBlock == approval)
            {
                header[field] = string.Empty;
                headerX[field] = "X";
            }
            else
            {
                notes.Add($"{label} {currentBlock} không phải block duyệt ({approval ?? "chưa cấu hình"}) — giữ nguyên.");
            }
        }

        Release("DLV_BLOCK", "Delivery block", current.DeliveryBlock, approvalDeliveryBlock);
        Release("BILL_BLOCK", "Billing block", current.BillingBlock, approvalBillingBlock);
        return new ContractApprovePlan(header.Count > 0, header, headerX, notes);
    }

    public static string ItemNo(int n) => n.ToString("000000");

    private static readonly ContractBapiInput Empty = new(
        new Dictionary<string, object?>(), new Dictionary<string, object?>(),
        Array.Empty<Row>(), Array.Empty<Row>(), Array.Empty<Row>(), Array.Empty<Row>(), Array.Empty<Row>());

    private static Row Partner(string role, string? customer) => new Dictionary<string, object?>
    {
        ["PARTN_ROLE"] = role,
        ["PARTN_NUMB"] = AccDocumentMapper.Alpha(customer, 10)
    };

    private static void AddItem(
        List<Row> items, List<Row> itemsX, int number, string flag,
        string? material, decimal? qty, string? unit, string? plant, string? shipPoint, string? text, string? rejection)
    {
        var item = new Dictionary<string, object?>
        {
            ["ITM_NUMBER"] = ItemNo(number),
            ["MATERIAL"] = AccDocumentMapper.Alpha(material, 18),
            ["TARGET_QTY"] = qty,
            ["TARGET_QU"] = SapRequestMapper.Code(unit),
            ["PLANT"] = SapRequestMapper.Code(plant),
            ["SHIP_POINT"] = SapRequestMapper.Code(shipPoint),
            ["SHORT_TEXT"] = SapRequestMapper.Text(text),
            ["REASON_REJ"] = SapRequestMapper.Code(rejection)
        };
        items.Add(item);
        var x = FlagsOf(item, flag);
        ((Dictionary<string, object?>)x)["ITM_NUMBER"] = ItemNo(number);
        itemsX.Add(x);
    }

    private static void AddData(List<Row> data, List<Row> dataX, int number, string flag, DateOnly? from, DateOnly? to)
    {
        var d = new Dictionary<string, object?>
        {
            ["ITM_NUMBER"] = ItemNo(number),
            ["CON_ST_DAT"] = SapRequestMapper.Dats(from),
            ["CON_EN_DAT"] = SapRequestMapper.Dats(to)
        };
        data.Add(d);
        var x = FlagsOf(d, flag);
        ((Dictionary<string, object?>)x)["ITM_NUMBER"] = ItemNo(number);
        dataX.Add(x);
    }

    /// <summary>Structure ...X: UPDATEFLAG + "X" cho mọi field có giá trị.</summary>
    private static Row FlagsOf(Row values, string updateFlag)
    {
        var x = new Dictionary<string, object?> { ["UPDATEFLAG"] = updateFlag };
        foreach (var (name, value) in values)
            if (name != "ITM_NUMBER" && value is not null)
                x[name] = "X";
        return x;
    }
}
