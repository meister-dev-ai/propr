// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     A stored binding can name a wire shape the family it sits on does not serve. That is safe only while
///     asking for one produces a refusal — the alternative is a driver falling through to the shape it does
///     speak and putting a request on the wire in the wrong format, which the provider answers with a rejection
///     naming nothing useful.
/// </summary>
public sealed class AiProtocolModeSupportTests
{
    private const string OpenAiKey = "meisterdev/openAi";

    private const string CompatibleKey = "meisterdev/openAiCompatible";

    private const string GatewayKey = "meisterdev/liteLlm";

    private const string OpenAiResponses = OpenAiKey + ":Responses";

    private const string CompatibleChatCompletions = CompatibleKey + ":ChatCompletions";

    // A wire shape another family declares, which a repointed connection's binding still holds.
    private const string AnotherFamilysProtocol = "meisterdev/anthropic:AnthropicMessages";

    [Fact]
    public void AShapeTheDriverSpeaksIsPermitted()
    {
        Assert.Null(
            AiProtocolModeSupport.GetRefusalReason(
                OpenAiKey,
                ResponsesShapes(OpenAiKey),
                OpenAiResponses));
    }

    [Fact]
    public void AShapeTheDriverCannotSpeakIsRefusedAndSaysWhatItCanSpeak()
    {
        var refusal = AiProtocolModeSupport.GetRefusalReason(
            CompatibleKey,
            CompatibleShapes(CompatibleKey),
            AnotherFamilysProtocol);

        Assert.NotNull(refusal);
        Assert.Contains(AnotherFamilysProtocol, refusal, StringComparison.Ordinal);
        Assert.Contains(CompatibleChatCompletions, refusal, StringComparison.Ordinal);
    }

    // The Responses API is an OpenAI-specific surface. Assuming it of an arbitrary compatible server turns into a
    // 404 on the first call, so it is absent from what a compatible endpoint is credited with.
    [Fact]
    public void ACompatibleEndpointIsNotCreditedWithTheResponsesApi()
    {
        Assert.DoesNotContain(
            CompatibleKey + ":Responses",
            CompatibleShapes(CompatibleKey));
        Assert.Contains(OpenAiResponses, ResponsesShapes(OpenAiKey));
    }

    [Fact]
    public void RequireThrowsWithAMessageAnOperatorCanActOn()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => AiProtocolModeSupport.Require(
            GatewayKey,
            CompatibleShapes(GatewayKey),
            AnotherFamilysProtocol));

        Assert.Contains(AnotherFamilysProtocol, failure.Message, StringComparison.Ordinal);
        Assert.Contains(GatewayKey, failure.Message, StringComparison.Ordinal);
    }

    // Auto means "the driver chooses", so it must not be able to choose a shape the driver cannot speak. A
    // catalog entry advertising the Responses API describes the vendor, not the endpoint it is reached through.
    [Fact]
    public void NarrowingStopsAutoFromChoosingAnUnspeakableShape()
    {
        var model = new ProviderModelDescriptor(
            Guid.NewGuid(),
            "gpt-4o",
            [
                ProviderDeclaredProtocolModes.Auto,
                CompatibleKey + ":Responses",
                CompatibleChatCompletions,
            ]);

        var narrowed = AiProtocolModeSupport.NarrowToSupported(
            model,
            CompatibleShapes(CompatibleKey));

        Assert.Equal(
            [ProviderDeclaredProtocolModes.Auto, CompatibleChatCompletions],
            narrowed.SupportedProtocolModes);
    }

    [Fact]
    public void NarrowingLeavesAModelTheDriverFullySupportsAlone()
    {
        var model = new ProviderModelDescriptor(
            Guid.NewGuid(),
            "gpt-4o",
            [ProviderDeclaredProtocolModes.Auto, OpenAiKey + ":ChatCompletions"]);

        Assert.Same(model, AiProtocolModeSupport.NarrowToSupported(model, ResponsesShapes(OpenAiKey)));
    }

    // The shapes a family speaks are the family's own declaration; these stand in for two of the shipped ones so
    // the assertions below are about the comparison and not about any family's list.
    private static IReadOnlyList<string> ResponsesShapes(string key)
    {
        return
        [
            ProviderDeclaredProtocolModes.Auto,
            ProviderVocabulary.Compose(key, "Responses"),
            ProviderVocabulary.Compose(key, "ChatCompletions"),
            ProviderDeclaredProtocolModes.Embeddings,
        ];
    }

    private static IReadOnlyList<string> CompatibleShapes(string key)
    {
        return
        [
            ProviderDeclaredProtocolModes.Auto,
            ProviderVocabulary.Compose(key, "ChatCompletions"),
            ProviderDeclaredProtocolModes.Embeddings,
        ];
    }
}
