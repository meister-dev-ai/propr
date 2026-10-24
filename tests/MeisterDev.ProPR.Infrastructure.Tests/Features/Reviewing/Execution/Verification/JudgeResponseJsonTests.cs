// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed class JudgeResponseJsonTests
{
    // The end of a judge answer whose last source quote ends in a brace, as recorded in reviews.
    private const string Head =
        """{"verdict":"confirmed","reason":"The guard is missing.","confidence_evidence":{"level":"high","assumptions":[],"source_quotes":[{"file_path":"src/A.ts","quote":"if (ready) {"}""";

    [Theory]
    [InlineData("""{"verdict":"confirmed","reason":"r"}""")]
    [InlineData("```json\n{\"verdict\":\"confirmed\",\"reason\":\"r\"}\n```")]
    [InlineData("""Verdict follows. {"verdict":"confirmed","reason":"r"}""")]
    public void AWellFormedAnswer_IsReadAsBefore(string answer)
    {
        using var document = JudgeResponseJson.TryParseObject(answer);

        Assert.Equal("confirmed", document!.RootElement.GetProperty("verdict").GetString());
    }

    [Theory]
    [InlineData(Head + "]} }\"}")]
    [InlineData(Head + "]}}}")]
    [InlineData(Head + "]}\"}")]
    [InlineData(Head + "]},\"confidence_evidence\"}")]
    public void AnAnswerWithADamagedEnd_KeepsTheVerdictAndTheEvidence(string answer)
    {
        using var document = JudgeResponseJson.TryParseObject(answer);

        var root = document!.RootElement;
        Assert.Equal("confirmed", root.GetProperty("verdict").GetString());
        var evidence = root.GetProperty("confidence_evidence");
        Assert.Equal("high", evidence.GetProperty("level").GetString());
        Assert.Equal("if (ready) {", Assert.Single(evidence.GetProperty("source_quotes").EnumerateArray()).GetProperty("quote").GetString());
    }

    [Fact]
    public void ABackslashThatStartsNoEscape_IsReadAsABackslash()
    {
        using var document = JudgeResponseJson.TryParseObject("""{"verdict":"not_confirmed","reason":"device.ID = \"keep\"\n\adevice.Save()"}""");

        Assert.Equal("device.ID = \"keep\"\n\\adevice.Save()", document!.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void ARawControlCharacterInsideAString_IsRead()
    {
        using var document = JudgeResponseJson.TryParseObject("{\"verdict\":\"confirmed\",\"reason\":\"first line\n\tsecond line\"}");

        Assert.Equal("first line\n\tsecond line", document!.RootElement.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("I cannot decide this.")]
    [InlineData("""{"verdict":"confir""")]
    [InlineData("""["confirmed"]""")]
    [InlineData("")]
    [InlineData(null)]
    public void TextWithoutAJsonObject_IsNotRead(string? answer)
    {
        Assert.Null(JudgeResponseJson.TryParseObject(answer));
    }
}
