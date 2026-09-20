// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Diagnostics;
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
            "meisterdev/openAiCompatible",
            "https://api.deepseek.com",
            "meisterdev/openAi:ApiKey",
            Secret);

        var rendered = $"{endpoint}";

        Assert.DoesNotContain(Secret, rendered, StringComparison.Ordinal);
        Assert.Contains("[redacted]", rendered, StringComparison.Ordinal);
        // The parts an operator needs in order to recognise which endpoint this was are still there.
        Assert.Contains("https://api.deepseek.com", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1", rendered, StringComparison.Ordinal);
        Assert.Contains("meisterdev/openAiCompatible", rendered, StringComparison.Ordinal);
    }

    // A header or a query parameter is where several providers expect the key, so their values are elided too and
    // only the names survive — enough to see that a header was configured, not enough to use it.
    [Fact]
    public void HeaderAndQueryValuesAreElidedButTheirNamesAreKept()
    {
        var endpoint = new ProviderEndpoint(
            "meisterdev/openAiCompatible",
            "https://api.example.com/v1",
            "meisterdev/openAi:ApiKey",
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
        var endpoint = new ProviderEndpoint("meisterdev/openAi", "https://api.openai.com/v1", "meisterdev/azureOpenAi:AzureIdentity");

        Assert.Contains("Secret = none", endpoint.ToString(), StringComparison.Ordinal);
    }

    // The other way a credential escapes without anyone writing a log line: a driver or a host serializing the
    // endpoint it was handed. An add-in can do that with its own keyed store, so the members that carry
    // credential material are excluded from serialization as well as from rendering.
    [Fact]
    public void AnEndpointSerializesWithoutAnythingThatCanCarryACredential()
    {
        var endpoint = new ProviderEndpoint(
            "meisterdev/openAiCompatible",
            "https://api.deepseek.com",
            "meisterdev/openAi:ApiKey",
            Secret,
            new Dictionary<string, string> { ["Authorization"] = $"Bearer {Secret}" },
            new Dictionary<string, string> { ["api-key"] = Secret });

        var json = JsonSerializer.Serialize(endpoint);

        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api-key", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://api.deepseek.com", json, StringComparison.Ordinal);
    }

    // Records compare and copy by value; overriding ToString must not have disturbed either.
    [Fact]
    public void ValueSemanticsSurviveTheOverride()
    {
        var endpoint = new ProviderEndpoint("meisterdev/openAi", "https://api.openai.com/v1", "meisterdev/openAi:ApiKey", Secret);

        Assert.Equal(endpoint, endpoint with { });
        Assert.Equal(Secret, (endpoint with { BaseUrl = "https://other.example" }).Secret);
    }

    // An operator is free to put a key in a base URL as well: userinfo carries one, and an '?api-key=' query
    // parameter is how several providers expect one. The path goes too: a provider that carries a key in it, and
    // a gateway that routes on a tenant segment, both put credential material there. What is left is the scheme
    // and the authority, which still names the host the line was about.
    [Fact]
    public void AnAddressRendersWithoutItsUserinfoOrItsQuery()
    {
        var rendered = SecretSafeRendering.Address("https://someone:sk-do-not-log-this@api.example.com/v1?api-key=sk-also-not-this#frag");

        Assert.Equal("https://api.example.com", rendered);
    }

    [Fact]
    public void AnEndpointRendersItsAddressWithoutACredentialInIt()
    {
        var endpoint = new ProviderEndpoint(
            "meisterdev/openAiCompatible",
            "https://api.deepseek.com/v1?api-key=" + Secret,
            "meisterdev/openAi:ApiKey",
            null);

        var rendered = $"{endpoint}";

        Assert.DoesNotContain(Secret, rendered, StringComparison.Ordinal);
        Assert.Contains("https://api.deepseek.com", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentAddressRendersAsAbsent(string? url)
    {
        Assert.Equal("none", SecretSafeRendering.Address(url));
    }

    [Fact]
    public void AnAddressThatDoesNotParseIsNotRenderedAtAll()
    {
        Assert.Equal("[unparsable]", SecretSafeRendering.Address("not a url " + Secret));
    }

    // A key name is written by an operator or by a provider family. A line break in one would end the log line
    // and let the rest of the name be read as a further line of its own.
    [Fact]
    public void AKeyNameCarryingALineBreakDoesNotBreakTheLine()
    {
        var rendered = SecretSafeRendering.KeyNames(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X-Api-Key\r\nlevel=Fatal message=\"forged\""] = "value",
            });

        Assert.DoesNotContain("\r", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", rendered, StringComparison.Ordinal);
        Assert.Contains("X-Api-Key", rendered, StringComparison.Ordinal);
    }
}
