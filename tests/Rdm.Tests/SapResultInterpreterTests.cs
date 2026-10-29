using FluentAssertions;
using Rdm.Application.Postings;
using Rdm.Sap;

namespace Rdm.Tests;

public class SapResultInterpreterTests
{
    private static SapReturnMessage Rw605(string objKey) =>
        new("S", "RW", "605", $"Document posted successfully: BKPFF {objKey} DRFCLNT930",
            MessageV1: "BKPFF", MessageV2: objKey, MessageV3: "DRFCLNT930");

    [Fact]
    public void Rw605_is_parsed_into_belnr_bukrs_gjahr()
    {
        var result = SapResultInterpreter.InterpretSelfCommitting(new[] { Rw605("010000012310002026") });

        result.Status.Should().Be(PostingStatus.Succeeded);
        result.Document.Should().BeEquivalentTo(new SapDocumentRef(
            "0100000123", "1000", "2026", "BKPFF", "010000012310002026", "DRFCLNT930"));
    }

    [Fact]
    public void V1_311_returns_sales_document_number()
    {
        var msg = new SapReturnMessage("S", "V1", "311", "VN CreditMemoRequest 60000123 has been saved",
            MessageV1: "VN CreditMemoRequest", MessageV2: "60000123");

        var result = SapResultInterpreter.InterpretSelfCommitting(new[] { msg });

        result.Status.Should().Be(PostingStatus.Succeeded);
        result.Document!.DocumentNumber.Should().Be("60000123");
    }

    [Fact]
    public void Error_messages_without_document_mean_failed()
    {
        var messages = new[]
        {
            new SapReturnMessage("E", "RW", "609", "Error in document: BKPFF $ DRFCLNT930"),
            new SapReturnMessage("E", "F5", "702", "Balance in transaction currency")
        };

        var result = SapResultInterpreter.InterpretSelfCommitting(messages);

        result.Status.Should().Be(PostingStatus.Failed);
        result.ErrorCode.Should().Be(SapErrorCodes.Business);
        result.ErrorMessage.Should().Contain("Balance in transaction currency");
        result.Messages.Should().HaveCount(2);
    }

    [Fact]
    public void Document_number_wins_over_side_errors_because_sap_already_committed()
    {
        var messages = new[] { new SapReturnMessage("E", "TD", "600", "Text error"), Rw605("010000012310002026") };

        SapResultInterpreter.InterpretSelfCommitting(messages).Status.Should().Be(PostingStatus.Succeeded);
    }

    [Fact]
    public void No_error_and_no_document_is_unknown()
    {
        var result = SapResultInterpreter.InterpretSelfCommitting(new[] { new SapReturnMessage("W", "XX", "001", "warning") });

        result.Status.Should().Be(PostingStatus.Unknown);
        result.ErrorCode.Should().Be(SapErrorCodes.UnknownOutcome);
    }

    [Fact]
    public void Non_fi_object_key_falls_back_to_first_10_chars()
    {
        SapResultInterpreter.FromObjectKey("VBRK", "9000001234", null).DocumentNumber.Should().Be("9000001234");
    }
}
