using Rdm.Application.Postings;
using Rdm.Application.Requests;
using SAP.Middleware.Connector;

namespace Rdm.Sap;

/// <summary>
/// Sale contract qua BAPI chuẩn — tách 3 nghiệp vụ:
///   - Tạo:  BAPI_CONTRACT_CREATEFROMDATA, gắn delivery + billing block duyệt (Sap:ContractApproval*Block, mặc định 30).
///   - Sửa:  BAPI_CUSTOMERCONTRACT_CHANGE trên hiện trạng đọc từ VBAK/VBAP/VEDA; mặc định gắn lại block duyệt.
///   - Duyệt: BAPI_CUSTOMERCONTRACT_CHANGE gỡ đúng block duyệt (block khác giữ nguyên).
/// Cả 3 BAPI không tự commit → BAPI + BAPI_TRANSACTION_COMMIT trong cùng stateful session.
/// Chỉ cho các loại hợp đồng trong Sap:ContractTypes (DRF-930 có thêm ZCQ/ZDCQ/ZCCQ của đơn vị khác).
/// </summary>
public sealed partial class SapClient
{
    private const string ContractCreateFunction = "BAPI_CONTRACT_CREATEFROMDATA";
    private const string ContractChangeFunction = "BAPI_CUSTOMERCONTRACT_CHANGE";
    private const string ContractCategory = "G";

    public Task<SapCallResult> CreateContractAsync(ContractCreateRequest request, CancellationToken ct) =>
        RunAsync(ContractCreateFunction, stateful: true, (dest, ctx) =>
        {
            var type = SapRequestMapper.Code(request.ContractType)!;
            if (!IsAllowedContractType(type))
                return SapCallResult.Failed(SapErrorCodes.Business,
                    $"Loại hợp đồng {type} không được phép (cho phép: {string.Join(", ", _options.ContractTypes)}).");

            var input = ContractMapper.MapCreate(request, _options.ContractApprovalDeliveryBlock, _options.ContractApprovalBillingBlock);
            var fn = dest.Repository.CreateFunction(ContractCreateFunction);
            SetFields(fn.GetStructure("CONTRACT_HEADER_IN"), input.Header);
            SetFields(fn.GetStructure("CONTRACT_HEADER_INX"), input.HeaderX);
            fn.SetValue("INT_NUMBER_ASSIGNMENT", "X");
            AppendRows(fn.GetTable("CONTRACT_ITEMS_IN"), input.Items);
            AppendRows(fn.GetTable("CONTRACT_ITEMS_INX"), input.ItemsX);
            AppendRows(fn.GetTable("CONTRACT_PARTNERS"), input.Partners);
            AppendRows(fn.GetTable("CONTRACT_DATA_IN"), input.Data);
            AppendRows(fn.GetTable("CONTRACT_DATA_INX"), input.DataX);

            var invokeError = Invoke(fn, dest, ctx, abapErrorMayFollowCommit: false);
            if (invokeError is not null)
            {
                Rollback(dest);
                return invokeError;
            }

            var messages = ReadReturn(fn.GetTable("RETURN"));
            var vbeln = fn.GetString("SALESDOCUMENT").Trim();
            if (vbeln.Length == 0)
            {
                Rollback(dest);
                var errors = messages.Where(m => m.IsError).Select(m => m.Message).ToList();
                return SapCallResult.Failed(SapErrorCodes.Business,
                    errors.Count > 0 ? string.Join("; ", errors) : "SAP không tạo hợp đồng và không trả lỗi.", messages);
            }

            return Commit(dest, messages, all => SapCallResult.Success(ContractRef(vbeln), all));
        }, ct);

    public Task<SapCallResult> ChangeContractAsync(ContractChangeRequest request, CancellationToken ct) =>
        RunAsync(ContractChangeFunction, stateful: true, (dest, ctx) =>
        {
            var current = ReadContract(dest, request.ContractNumber, out var readError);
            if (current is null) return readError!;

            var input = ContractMapper.MapChange(request, current,
                _options.ContractApprovalDeliveryBlock, _options.ContractApprovalBillingBlock, out var mapError);
            if (mapError is not null) return SapCallResult.Failed(SapErrorCodes.Business, mapError);

            var fn = dest.Repository.CreateFunction(ContractChangeFunction);
            fn.SetValue("SALESDOCUMENT", current.ContractNumber);
            SetFields(fn.GetStructure("CONTRACT_HEADER_IN"), input.Header);
            SetFields(fn.GetStructure("CONTRACT_HEADER_INX"), input.HeaderX);
            AppendRows(fn.GetTable("CONTRACT_ITEM_IN"), input.Items);
            AppendRows(fn.GetTable("CONTRACT_ITEM_INX"), input.ItemsX);
            AppendRows(fn.GetTable("CONTRACT_DATA_IN"), input.Data);
            AppendRows(fn.GetTable("CONTRACT_DATA_INX"), input.DataX);

            return InvokeChangeAndCommit(fn, dest, ctx, current.ContractNumber, Array.Empty<SapReturnMessage>());
        }, ct);

    public Task<SapCallResult> ApproveContractAsync(ContractApproveRequest request, CancellationToken ct) =>
        RunAsync(ContractChangeFunction, stateful: true, (dest, ctx) =>
        {
            var current = ReadContract(dest, request.ContractNumber, out var readError);
            if (current is null) return readError!;

            var plan = ContractMapper.MapApprove(current, _options.ContractApprovalDeliveryBlock, _options.ContractApprovalBillingBlock);
            var notes = plan.Notes.Select(n => new SapReturnMessage("W", "RDM", "APPROVE", n)).ToList();
            if (!plan.Required)
            {
                notes.Insert(0, new SapReturnMessage("S", "RDM", "APPROVE",
                    $"Hợp đồng {current.ContractNumber} không còn block duyệt — không cần gọi SAP."));
                return SapCallResult.Success(ContractRef(current.ContractNumber), notes);
            }

            var fn = dest.Repository.CreateFunction(ContractChangeFunction);
            fn.SetValue("SALESDOCUMENT", current.ContractNumber);
            SetFields(fn.GetStructure("CONTRACT_HEADER_IN"), plan.Header);
            SetFields(fn.GetStructure("CONTRACT_HEADER_INX"), plan.HeaderX);

            return InvokeChangeAndCommit(fn, dest, ctx, current.ContractNumber, notes);
        }, ct);

    private SapCallResult InvokeChangeAndCommit(
        IRfcFunction fn, RfcDestination dest, CallContext ctx, string contractNumber, IReadOnlyList<SapReturnMessage> extra)
    {
        var invokeError = Invoke(fn, dest, ctx, abapErrorMayFollowCommit: false);
        if (invokeError is not null)
        {
            Rollback(dest);
            return invokeError;
        }

        var messages = ReadReturn(fn.GetTable("RETURN"));
        messages.AddRange(extra);
        var errors = messages.Where(m => m.IsError).ToList();
        if (errors.Count > 0)
        {
            Rollback(dest);
            return SapCallResult.Failed(SapErrorCodes.Business, string.Join("; ", errors.Select(e => e.Message)), messages);
        }

        return Commit(dest, messages, all => SapCallResult.Success(ContractRef(contractNumber), all));
    }

    private bool IsAllowedContractType(string type) =>
        _options.ContractTypes.Any(t => string.Equals(t.Trim(), type, StringComparison.OrdinalIgnoreCase));

    private static SapDocumentRef ContractRef(string vbeln) => new(vbeln, ObjectType: "VBAK", ObjectKey: vbeln);

    /// <summary>Đọc VBAK + VBAP + VEDA (chưa ghi gì → lỗi ở đây là Failed).</summary>
    private ContractSnapshot? ReadContract(RfcDestination dest, string contractNumber, out SapCallResult? error)
    {
        error = null;
        var vbeln = AccDocumentMapper.Alpha(contractNumber, 10)!;
        var batch = _options.ReadTableBatchSize;

        var header = RfcTableReader.Read(dest, "VBAK", new[] { "VBELN", "VBTYP", "AUART", "LIFSK", "FAKSK", "GUEBG", "GUEEN" },
            new[] { RfcTableReader.Condition("VBELN", vbeln) }, 1).FirstOrDefault();
        if (header is null)
        {
            error = SapCallResult.Failed(SapErrorCodes.Business, $"Không tìm thấy chứng từ bán hàng {vbeln} trên SAP.");
            return null;
        }
        if (header["VBTYP"] != ContractCategory)
        {
            error = SapCallResult.Failed(SapErrorCodes.Business, $"{vbeln} không phải hợp đồng (VBTYP = {header["VBTYP"]}).");
            return null;
        }
        if (!IsAllowedContractType(header["AUART"]))
        {
            error = SapCallResult.Failed(SapErrorCodes.Business,
                $"Hợp đồng {vbeln} loại {header["AUART"]} không thuộc loại API được phép ({string.Join(", ", _options.ContractTypes)}).");
            return null;
        }

        var items = RfcTableReader.Read(dest, "VBAP", new[] { "POSNR", "MATNR", "ABGRU" },
                new[] { RfcTableReader.Condition("VBELN", vbeln) }, batch)
            .Select(r => new ContractSnapshotItem(int.Parse(r["POSNR"]), r["MATNR"], RfcTableReader.NullIfEmpty(r["ABGRU"])))
            .ToList();

        var dataItems = RfcTableReader.Read(dest, "VEDA", new[] { "VPOSN" },
                new[] { RfcTableReader.Condition("VBELN", vbeln) }, batch)
            .Select(r => int.Parse(r["VPOSN"]))
            .ToHashSet();

        return new ContractSnapshot(vbeln, header["VBTYP"], header["AUART"],
            RfcTableReader.NullIfEmpty(header["LIFSK"]), RfcTableReader.NullIfEmpty(header["FAKSK"]),
            RfcTableReader.ParseDate(header["GUEBG"]), RfcTableReader.ParseDate(header["GUEEN"]),
            items, dataItems);
    }
}
