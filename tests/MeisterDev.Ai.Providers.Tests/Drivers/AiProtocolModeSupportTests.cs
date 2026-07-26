// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     The protocol enum names wire shapes no current driver implements. That is safe only while asking for one
///     produces a refusal — the alternative is a driver falling through to the shape it does speak and putting a
///     request on the wire in the wrong format, which the provider answers with a rejection naming nothing useful.
/// </summary>
public sealed class AiProtocolModeSupportTests
{
    [Fact]
    public void AShapeTheDriverSpeaksIsPermitted()
    {
        Assert.Null(
            AiProtocolModeSupport.DescribeRefusal(
                AiProviderKind.OpenAi,
                AiProtocolModeSupport.OpenAiFamily,
                AiProtocolMode.Responses));
    }

    [Fact]
    public void AShapeTheDriverCannotSpeakIsRefusedAndSaysWhatItCanSpeak()
    {
        var refusal = AiProtocolModeSupport.DescribeRefusal(
            AiProviderKind.OpenAiCompatible,
            AiProtocolModeSupport.OpenAiCompatibleFamily,
            AiProtocolMode.AnthropicMessages);

        Assert.NotNull(refusal);
        Assert.Contains("AnthropicMessages", refusal, StringComparison.Ordinal);
        Assert.Contains("ChatCompletions", refusal, StringComparison.Ordinal);
    }

    // The Responses API is an OpenAI-specific surface. Assuming it of an arbitrary compatible server turns into a
    // 404 on the first call, so it is absent from what a compatible endpoint is credited with.
    [Fact]
    public void ACompatibleEndpointIsNotCreditedWithTheResponsesApi()
    {
        Assert.DoesNotContain(AiProtocolMode.Responses, AiProtocolModeSupport.OpenAiCompatibleFamily);
        Assert.Contains(AiProtocolMode.Responses, AiProtocolModeSupport.OpenAiFamily);
    }

    [Fact]
    public void RequireThrowsWithAMessageAnOperatorCanActOn()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => AiProtocolModeSupport.Require(
            AiProviderKind.LiteLlm,
            AiProtocolModeSupport.OpenAiCompatibleFamily,
            AiProtocolMode.BedrockConverse));

        Assert.Contains("BedrockConverse", failure.Message, StringComparison.Ordinal);
        Assert.Contains("LiteLlm", failure.Message, StringComparison.Ordinal);
    }

    // Auto means "the driver chooses", so it must not be able to choose a shape the driver cannot speak. A
    // catalog entry advertising the Responses API describes the vendor, not the endpoint it is reached through.
    [Fact]
    public void NarrowingStopsAutoFromChoosingAnUnspeakableShape()
    {
        var model = new ProviderModelDescriptor(
            Guid.NewGuid(),
            "gpt-4o",
            [AiProtocolMode.Auto, AiProtocolMode.Responses, AiProtocolMode.ChatCompletions]);

        var narrowed = AiProtocolModeSupport.NarrowToSupported(model, AiProtocolModeSupport.OpenAiCompatibleFamily);

        Assert.Equal([AiProtocolMode.Auto, AiProtocolMode.ChatCompletions], narrowed.SupportedProtocolModes);
    }

    [Fact]
    public void NarrowingLeavesAModelTheDriverFullySupportsAlone()
    {
        var model = new ProviderModelDescriptor(Guid.NewGuid(), "gpt-4o", [AiProtocolMode.Auto, AiProtocolMode.ChatCompletions]);

        Assert.Same(model, AiProtocolModeSupport.NarrowToSupported(model, AiProtocolModeSupport.OpenAiFamily));
    }
}
