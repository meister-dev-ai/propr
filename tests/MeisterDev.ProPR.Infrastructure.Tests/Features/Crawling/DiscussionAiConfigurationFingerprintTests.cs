// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Crawling.Persistence;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Crawling;

/// <summary>
///     The AI configuration fingerprint decides whether memory and insights process a discussion again. A configuration
///     that did not change keeps its fingerprint across the change from efforts to level names, so the upgrade does not
///     start that work again.
/// </summary>
public sealed class DiscussionAiConfigurationFingerprintTests
{
    /// <summary>The enumeration the fingerprint held before levels had names.</summary>
    public enum FormerEffort
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3,
    }

    [Theory]
    [InlineData(null, FormerEffort.None)]
    [InlineData("low", FormerEffort.Low)]
    [InlineData("medium", FormerEffort.Medium)]
    [InlineData("high", FormerEffort.High)]
    public void ALogicalModel_KeepsTheFingerprintOfItsFormerEffort(string? level, FormerEffort former)
    {
        var connectionId = Guid.NewGuid();
        var modelId = Guid.NewGuid();

        var before = DiscussionFingerprints.CanonicalValue(
            new { Name = "reviewer", ConnectionId = connectionId, ConfiguredModelId = modelId, ProtocolMode = "auto", ReasoningEffort = former });
        var now = DiscussionFingerprints.CanonicalValue(
            DiscussionAiConfigurationVersionReader.LogicalModelRow("reviewer", connectionId, modelId, "auto", level));

        Assert.Equal(before, now);
    }

    [Fact]
    public void ALevelThatNoFormerEffortNamed_ChangesTheFingerprint()
    {
        var connectionId = Guid.NewGuid();
        var modelId = Guid.NewGuid();

        var high = DiscussionFingerprints.CanonicalValue(
            DiscussionAiConfigurationVersionReader.LogicalModelRow("reviewer", connectionId, modelId, "auto", "high"));
        var xhigh = DiscussionFingerprints.CanonicalValue(
            DiscussionAiConfigurationVersionReader.LogicalModelRow("reviewer", connectionId, modelId, "auto", "xhigh"));
        var none = DiscussionFingerprints.CanonicalValue(
            DiscussionAiConfigurationVersionReader.LogicalModelRow("reviewer", connectionId, modelId, "auto", "none"));
        var unset = DiscussionFingerprints.CanonicalValue(
            DiscussionAiConfigurationVersionReader.LogicalModelRow("reviewer", connectionId, modelId, "auto", null));

        Assert.NotEqual(high, xhigh);
        Assert.NotEqual(unset, none);
    }

    // A purpose assignment without a level keeps the fingerprint it had before assignments carried levels; a level set
    // on the assignment changes the fingerprint, as a level set on the logical model does.
    [Fact]
    public void APurposeAssignment_ChangesTheFingerprintOnlyWithALevel()
    {
        var before = DiscussionFingerprints.CanonicalValue(new { LogicalModelName = "reviewer", Purpose = AiPurpose.MemoryReconsideration });

        var withoutLevel = DiscussionFingerprints.CanonicalValue(
            DiscussionAiConfigurationVersionReader.PurposeRow("reviewer", AiPurpose.MemoryReconsideration, null));
        var withLevel = DiscussionFingerprints.CanonicalValue(
            DiscussionAiConfigurationVersionReader.PurposeRow("reviewer", AiPurpose.MemoryReconsideration, "high"));

        Assert.Equal(before, withoutLevel);
        Assert.NotEqual(before, withLevel);
    }

    // A purpose binding without a level keeps the fingerprint it had before bindings carried levels; a level set on the
    // binding changes the fingerprint.
    [Fact]
    public void APurposeBinding_ChangesTheFingerprintOnlyWithALevel()
    {
        var id = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var settings = new Dictionary<string, string> { ["project"] = "review" };
        var before = DiscussionFingerprints.CanonicalValue(
            new
            {
                Id = id, ConnectionProfileId = connectionId, ConfiguredModelId = modelId, ProtocolMode = "Auto", IsEnabled = true,
                Purpose = "ReviewDefault", ProfileActive = true, ProviderKind = "meisterdev/openAi", DisplayName = "OpenAI",
                BaseUrl = "https://api.openai.com/v1", ProviderSettings = settings, Model = "gpt-6.1-terra",
            });

        DiscussionAiConfigurationVersionReader.BindingFingerprintRow Row(string? level) => new(
            id, connectionId, modelId, "Auto", true, "ReviewDefault", true, "meisterdev/openAi", "OpenAI",
            "https://api.openai.com/v1", settings, "gpt-6.1-terra", level);

        Assert.Equal(before, DiscussionFingerprints.CanonicalValue(Row(null)));
        Assert.NotEqual(before, DiscussionFingerprints.CanonicalValue(Row("high")));
    }
}
