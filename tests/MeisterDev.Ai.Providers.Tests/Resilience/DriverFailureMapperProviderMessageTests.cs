// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Transport;

namespace MeisterDev.Ai.Providers.Tests.Resilience;

/// <summary>
///     Covers how the message a provider wrote about a refused call is read out of the response body. An operator
///     reads that excerpt in the trace beside the status, and it is often the only statement of which setting to
///     change.
/// </summary>
/// <remarks>
///     Only the message field is read. Some gateways repeat the request in their error body, and the request holds
///     the code under review, so the rest of the body must not reach the excerpt.
/// </remarks>
public sealed class DriverFailureMapperProviderMessageTests
{
    [Fact]
    public void TheMessageOfAnOpenAiErrorIsReadWithTheParameterItNames()
    {
        var excerpt = DriverFailureMapper.ReadProviderMessage(
            """
            {"error":{"message":"Function tools with reasoning_effort are not supported for gpt-6-luna in /v1/chat/completions.",
            "type":"invalid_request_error","param":"reasoning_effort","code":"unsupported_parameter"}}
            """);

        Assert.Equal(
            "Function tools with reasoning_effort are not supported for gpt-6-luna in /v1/chat/completions. (parameter: reasoning_effort)",
            excerpt);
    }

    [Fact]
    public void AnErrorThatNamesNoParameterIsReadAsItsMessageAlone()
    {
        var excerpt = DriverFailureMapper.ReadProviderMessage(
            """{"error":{"code":400,"message":"Unable to submit request because it has an empty text parameter.","status":"INVALID_ARGUMENT"}}""");

        Assert.Equal("Unable to submit request because it has an empty text parameter.", excerpt);
    }

    // Servers that speak an OpenAI-compatible protocol do not all nest the message in an error object: some send
    // the error as a string, and others put the message at the top level of the body.
    [Theory]
    [InlineData("""{"error":"model 'qwen3:32b' not found"}""", "model 'qwen3:32b' not found")]
    [InlineData(
        """{"object":"error","message":"max_tokens must be at least 1, got 0.","type":"BadRequestError","param":"max_tokens","code":400}""",
        "max_tokens must be at least 1, got 0. (parameter: max_tokens)")]
    public void TheOtherShapesOfAnErrorBodyAreRead(string body, string expected)
    {
        Assert.Equal(expected, DriverFailureMapper.ReadProviderMessage(body));
    }

    [Fact]
    public void ARequestRepeatedBesideTheMessageIsLeftOut()
    {
        var excerpt = DriverFailureMapper.ReadProviderMessage(
            """{"error":{"message":"Invalid prompt.","request":{"messages":[{"role":"user","content":"the code under review"}]}}}""");

        Assert.Equal("Invalid prompt.", excerpt);
    }

    // A proxy in front of the provider answers with a page of its own, and a body that is not JSON has no message
    // field to read. A message that is not a string is not read either, because rendering an object would put
    // whatever it holds into the trace.
    [Theory]
    [InlineData("<html><head><title>502 Bad Gateway</title></head><body><h1>502 Bad Gateway</h1></body></html>")]
    [InlineData("upstream connect error or disconnect/reset before headers")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("""[{"error":{"message":"inside an array"}}]""")]
    [InlineData("""{"error":{"message":{"detail":"upstream closed"}}}""")]
    [InlineData("""{"error":{"code":502,"message":502}}""")]
    [InlineData("""{"detail":"Not Found"}""")]
    [InlineData("""{"error":{"message":"   "}}""")]
    [InlineData("""{"error":{"message":"cut off""")]
    public void ABodyWithoutAReadableMessageFieldYieldsNoExcerpt(string? body)
    {
        Assert.Null(DriverFailureMapper.ReadProviderMessage(body));
    }

    [Fact]
    public void AnOverlongMessageIsCutToTheFixedLengthAndKeepsTheParameter()
    {
        var body = $$$"""{"error":{"message":"{{{new string('x', 5000)}}}","param":"messages"}}""";

        var excerpt = DriverFailureMapper.ReadProviderMessage(body);

        Assert.NotNull(excerpt);
        Assert.Equal(DriverFailureMapper.MaximumProviderMessageLength, excerpt.Length);
        Assert.EndsWith("… (parameter: messages)", excerpt, StringComparison.Ordinal);
    }

    // A parameter is a name. One that runs on is cut as well, so it cannot push the message out of the excerpt.
    [Fact]
    public void AnOverlongParameterIsCutAndLeavesRoomForTheMessage()
    {
        var body = $$$"""{"error":{"message":"Invalid value.","param":"{{{new string('p', 5000)}}}"}}""";

        var excerpt = DriverFailureMapper.ReadProviderMessage(body);

        Assert.NotNull(excerpt);
        Assert.StartsWith("Invalid value. (parameter: ppp", excerpt, StringComparison.Ordinal);
        Assert.True(excerpt.Length < DriverFailureMapper.MaximumProviderMessageLength);
    }

    // The excerpt is written into one line of the trace and into log lines, so a line break in the provider's
    // message must not start a new one.
    [Fact]
    public void LineBreaksAndControlCharactersBecomeSingleSpaces()
    {
        var excerpt = DriverFailureMapper.ReadProviderMessage("{\"error\":{\"message\":\"  first line\\r\\n\\nsecond line\\t\\u0007third \\n\"}}");

        Assert.Equal("first line second line third", excerpt);
    }

    [Fact]
    public void TheBytesOfABodyAreReadTheSameWayAsItsText()
    {
        const string body =
            """{"error":{"message":"Unsupported value: 'temperature' does not support 0.2 with this model.","param":"temperature"}}""";

        Assert.Equal(
            DriverFailureMapper.ReadProviderMessage(body),
            DriverFailureMapper.ReadProviderMessage(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void ABodyWhoseBytesStartWithAByteOrderMarkIsStillRead()
    {
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("""{"error":{"message":"Invalid prompt."}}"""))
            .ToArray();

        Assert.Equal("Invalid prompt.", DriverFailureMapper.ReadProviderMessage(bytes));
    }

    // The bound is the one the host applies to every document it parses from a provider.
    [Fact]
    public void ABodyLargerThanTheHostParsesYieldsNoExcerpt()
    {
        var body = $$"""{"error":{"message":"Invalid prompt."},"echo":"{{new string('x', ProviderResponseBody.MaximumDocumentBytes)}}"}""";

        Assert.Null(DriverFailureMapper.ReadProviderMessage(body));
        Assert.Null(DriverFailureMapper.ReadProviderMessage(Encoding.UTF8.GetBytes(body)));
    }
}
