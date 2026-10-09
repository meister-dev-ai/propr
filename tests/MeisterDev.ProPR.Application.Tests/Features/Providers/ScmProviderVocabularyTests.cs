// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.Providers.Support;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Application.Tests.Features.Providers;

public sealed class ScmProviderVocabularyTests
{
    [Fact]
    public void CompatibilityDefaultsDeclareTheirNativeOwnership()
    {
        Assert.Equal(ScmProvider.AzureDevOps, ScmProviderVocabulary.AzureDevOpsCompatibilityDefault);
        Assert.Equal(WebhookProviderType.AzureDevOps, ScmProviderVocabulary.AzureDevOpsCompatibilityWebhookDefault);
    }

    [Fact]
    public void ApplicationDoesNotExposeNativeDiscoveryCompatibilityPorts()
    {
        Assert.Null(
            typeof(MeisterDev.ProPR.Application.Interfaces.IProviderAdminDiscoveryService).Assembly
                .GetType("MeisterDev.ProPR.Application.Interfaces.IAdoCompatibilityAdminDiscoveryService"));
    }

    [Theory]
    [InlineData("azureDevOps", ScmProvider.AzureDevOps)]
    [InlineData("github", ScmProvider.GitHub)]
    [InlineData("gitLab", ScmProvider.GitLab)]
    [InlineData("forgejo", ScmProvider.Forgejo)]
    public void ManagementRoundTripsExactPublicNames(string name, ScmProvider expected)
    {
        Assert.True(ScmProviderVocabulary.TryParseManagement(name, out var provider));
        Assert.Equal(expected, provider);
        Assert.Equal(name, ScmProviderVocabulary.ToPublicName(expected));
    }

    [Theory]
    [InlineData("GitHub")]
    [InlineData(" github")]
    [InlineData("github ")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("unknown")]
    public void ManagementRejectsNoncanonicalInputs(string input)
    {
        Assert.False(ScmProviderVocabulary.TryParseManagement(input, out _));
    }

    [Fact]
    public void NullManagementFilterRemainsUnfiltered()
    {
        Assert.True(ScmProviderVocabulary.TryParseManagement(null, out var provider));
        Assert.Null(provider);
    }

    [Theory]
    [InlineData("ADO", ScmProvider.AzureDevOps)]
    [InlineData("azureDevOps", ScmProvider.AzureDevOps)]
    [InlineData("GitHub", ScmProvider.GitHub)]
    [InlineData("gitlab", ScmProvider.GitLab)]
    [InlineData("forgejo", ScmProvider.Forgejo)]
    [InlineData(" 1 ", ScmProvider.GitHub)]
    [InlineData("999", (ScmProvider)999)]
    public void WebhooksPreserveExistingAliasAndEnumAcceptance(string input, ScmProvider expected)
    {
        Assert.True(ScmProviderVocabulary.TryParseWebhook(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UndefinedWebhookMappingRetainsStrictFailureAndTryOutcome()
    {
        Assert.False(ScmProviderVocabulary.TryFromWebhook((WebhookProviderType)999, out _));
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => ScmProviderVocabulary.FromWebhook((WebhookProviderType)999));
        Assert.Equal("provider", error.ParamName);
        Assert.Equal((WebhookProviderType)999, error.ActualValue);
        Assert.True(ScmProviderVocabulary.TryParseWebhook("999", out var parsed));
        Assert.Equal((ScmProvider)999, parsed);
    }

    [Theory]
    [InlineData(WebhookProviderType.AzureDevOps, "ado", "azuredevops")]
    [InlineData(WebhookProviderType.GitHub, "github", "github")]
    [InlineData(WebhookProviderType.GitLab, "gitlab", "gitlab")]
    [InlineData(WebhookProviderType.Forgejo, "forgejo", "forgejo")]
    public void WebhookAndTelemetryContextsPreserveSpellings(WebhookProviderType provider, string segment, string telemetry)
    {
        Assert.Equal(segment, ScmProviderVocabulary.ToWebhookSegment(provider));
        Assert.Equal(telemetry, ScmProviderVocabulary.ToTelemetryTag(ScmProviderVocabulary.FromWebhook(provider)));
    }
}
