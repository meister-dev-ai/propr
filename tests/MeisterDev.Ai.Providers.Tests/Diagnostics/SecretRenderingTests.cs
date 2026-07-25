// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Tests.Diagnostics;

/// <summary>
///     Proves that rendering a credential-bearing type does not render the credential. This is the leak that needs
///     no misconfiguration to happen: a record's generated <c>ToString</c> prints every property, so one ordinary
///     interpolation is enough, and nothing about the call site looks wrong.
/// </summary>
public sealed class SecretRenderingTests
{
    private const string Secret = "sk-do-not-log-this";

    [Fact]
    public void AnEndpointRendersWithoutItsSecret()
    {
        var endpoint = new ProviderEndpoint(
            AiProviderKind.OpenAiCompatible,
            "https://api.deepseek.com/v1",
            AiAuthMode.ApiKey,
            Secret);

        var rendered = $"{endpoint}";

        Assert.DoesNotContain(Secret, rendered, StringComparison.Ordinal);
        Assert.Contains("[redacted]", rendered, StringComparison.Ordinal);
        // The parts an operator needs in order to recognise which endpoint this was are still there.
        Assert.Contains("https://api.deepseek.com/v1", rendered, StringComparison.Ordinal);
        Assert.Contains("OpenAiCompatible", rendered, StringComparison.Ordinal);
    }

    // A header or a query parameter is where several providers expect the key, so their values are elided too and
    // only the names survive — enough to see that a header was configured, not enough to use it.
    [Fact]
    public void HeaderAndQueryValuesAreElidedButTheirNamesAreKept()
    {
        var endpoint = new ProviderEndpoint(
            AiProviderKind.OpenAiCompatible,
            "https://api.example.com/v1",
            AiAuthMode.ApiKey,
            DefaultHeaders: new Dictionary<string, string> { ["Authorization"] = $"Bearer {Secret}" },
            DefaultQueryParams: new Dictionary<string, string> { ["api-key"] = Secret });

        var rendered = endpoint.ToString();

        Assert.DoesNotContain(Secret, rendered, StringComparison.Ordinal);
        Assert.Contains("Authorization", rendered, StringComparison.Ordinal);
        Assert.Contains("api-key", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEndpointWithNoSecretSaysSoRatherThanShowingNothing()
    {
        var endpoint = new ProviderEndpoint(AiProviderKind.OpenAi, "https://api.openai.com/v1", AiAuthMode.AzureIdentity);

        Assert.Contains("Secret = none", endpoint.ToString(), StringComparison.Ordinal);
    }

    // Records compare and copy by value; overriding ToString must not have disturbed either.
    [Fact]
    public void ValueSemanticsSurviveTheOverride()
    {
        var endpoint = new ProviderEndpoint(AiProviderKind.OpenAi, "https://api.openai.com/v1", AiAuthMode.ApiKey, Secret);

        Assert.Equal(endpoint, endpoint with { });
        Assert.Equal(Secret, (endpoint with { BaseUrl = "https://other.example" }).Secret);
    }
}
