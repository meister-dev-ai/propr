// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Tests.AI;

/// <summary>
///     Covers the adapter boundary between the product's stored configuration and the provider library's
///     contract. The descriptor is the only channel by which host-held model metadata reaches the library, so a
///     field the library needs and the descriptor drops is a silently broken feature rather than a compile error.
/// </summary>
public sealed class ProviderContractMappingTests
{
    private const string CompatibleApiKey = "meisterdev/openAiCompatible:ApiKey";

    private const string CompatibleChatCompletions = "meisterdev/openAiCompatible:ChatCompletions";

    // A default level the provider reports reaches the discovered model when it is a level name that the reported
    // levels hold, because the save refuses any other.
    [Theory]
    [InlineData("none", new[] { "none", "low", "high" }, "none")]
    [InlineData(" High ", null, "high")]
    [InlineData("max", new[] { "low", "high" }, null)]
    [InlineData("two words", null, null)]
    [InlineData(null, new[] { "low" }, null)]
    public void DiscoveredModelCarriesTheDefaultLevelTheProviderReports(string? reported, string[]? levels, string? expected)
    {
        var model = new ProviderDiscoveredModel("gpt-6.1-terra", "GPT 6.1 Terra", [AiOperationKind.Chat], ["auto"])
        {
            ReasoningLevels = levels,
            DefaultReasoningLevel = reported,
        };

        var discovered = Assert.Single(new ProviderModelDiscoveryResult("ok", true, [], [model]).ToDto(DateTimeOffset.UnixEpoch).Models);

        Assert.Equal(expected, discovered.DefaultReasoningLevel);
    }

    [Fact]
    public void ConnectionMapsToTheEndpointADriverNeeds()
    {
        var connection = new AiConnectionDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Profile",
            "meisterdev/openAiCompatible",
            "https://api.deepseek.com/v1",
            CompatibleApiKey,
            AiDiscoveryMode.ManualOnly,
            true,
            [],
            [],
            AiVerificationResultDto.NeverVerified,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["X-Trace"] = "1" },
            new Dictionary<string, string> { ["api-version"] = "v1" },
            "secret-material");

        var endpoint = connection.ToProviderEndpoint();

        Assert.Equal("meisterdev/openAiCompatible", endpoint.ProviderKind);
        Assert.Equal("https://api.deepseek.com/v1", endpoint.BaseUrl);
        Assert.Equal(CompatibleApiKey, endpoint.AuthMode);
        Assert.Equal("secret-material", endpoint.Secret);
        Assert.Equal("1", endpoint.DefaultHeaders?["X-Trace"]);
        Assert.Equal("v1", endpoint.DefaultQueryParams?["api-version"]);
    }

    // The quirk a normalizing stage acts on has to survive the boundary, or the stage can never fire and the
    // catalog metadata that describes it is inert.
    [Fact]
    public void ModelCarriesTheReasoningContentQuirkToTheDriver()
    {
        var descriptor = Model(reasoningContentField: "reasoning_content").ToProviderModel();

        Assert.Equal("reasoning_content", descriptor.ReasoningContentField);
    }

    [Fact]
    public void ModelWithoutTheQuirkCarriesNothing()
    {
        Assert.Null(Model(reasoningContentField: null).ToProviderModel().ReasoningContentField);
    }

    [Fact]
    public void ModelReducesToWhatADriverAddressesItBy()
    {
        var model = Model(reasoningContentField: null);

        var descriptor = model.ToProviderModel();

        Assert.Equal(model.Id, descriptor.Id);
        Assert.Equal("deepseek-reasoner", descriptor.RemoteModelId);
        Assert.Equal([ProviderDeclaredProtocolModes.Auto, CompatibleChatCompletions], descriptor.SupportedProtocolModes);
    }

    private static AiConfiguredModelDto Model(string? reasoningContentField)
    {
        return new AiConfiguredModelDto(
            Guid.NewGuid(),
            "deepseek-reasoner",
            "DeepSeek Reasoner",
            [AiOperationKind.Chat],
            [ProviderDeclaredProtocolModes.Auto, CompatibleChatCompletions],
            SupportsReasoning: true,
            ReasoningContentField: reasoningContentField);
    }
}
