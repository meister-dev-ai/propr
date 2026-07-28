// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     Covers the plain OpenAI profile. What distinguishes it from the compatible profile is a single rule: an
///     Azure-hosted endpoint is refused rather than served.
/// </summary>
/// <remarks>
///     Refusing is the useful behaviour even though the request would partly work. Azure authenticates
///     differently and can use a managed identity instead of a key, so a profile stored under this kind would
///     either fail on its first call or lock the operator out of the keyless option. Naming the right provider
///     kind at configuration time is the whole point.
/// </remarks>
public sealed class OpenAiProviderDriverTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/")]
    public void TheVendorEndpointIsAccepted(string baseUrl)
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, AiAuthMode.ApiKey, HasApiKey: true)));
    }

    [Theory]
    [InlineData("https://contoso.openai.azure.com/")]
    [InlineData("https://contoso.services.ai.azure.com/")]
    [InlineData("https://contoso.cognitiveservices.azure.com/")]
    public void AnAzureHostIsRefusedAndNamesTheProviderKindThatFits(string baseUrl)
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, AiAuthMode.ApiKey, HasApiKey: true));

        Assert.NotNull(refusal);
        Assert.Contains("azureOpenAi", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingKeyIsRefused()
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget("https://api.openai.com/v1", AiAuthMode.ApiKey, HasApiKey: false));

        Assert.NotNull(refusal);
        Assert.Contains("API key", refusal, StringComparison.OrdinalIgnoreCase);
    }

    // The vendor endpoint is fixed and public, so there is no legitimate reason for this profile to point inward
    // even when an operator has opened private egress for its self-hosted neighbours.
    [Theory]
    [InlineData("https://127.0.0.1/v1")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    public void APrivateAddressIsRefusedByDefault(string baseUrl)
    {
        Assert.NotNull(Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, AiAuthMode.ApiKey, HasApiKey: true)));
    }

    [Fact]
    public void PlainHttpIsRefusedUnlessTheInsecureSchemeIsPermitted()
    {
        var target = new AiProbeTarget("http://api.openai.com/v1", AiAuthMode.ApiKey, HasApiKey: true);

        Assert.NotNull(Driver().ValidateProbeTarget(target));
        Assert.Null(Driver(allowInsecureScheme: true).ValidateProbeTarget(target));
    }

    // The opt-in covers where a request may go, not whether it is encrypted, so opening private egress must not
    // quietly also permit plaintext.
    [Fact]
    public void ThePrivateEgressOptInDoesNotRelaxTheScheme()
    {
        var target = new AiProbeTarget("http://10.0.0.5/v1", AiAuthMode.ApiKey, HasApiKey: true);

        var refusal = Driver(allowPrivateEgress: true).ValidateProbeTarget(target);

        Assert.NotNull(refusal);
        Assert.Contains("https", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDriverAnswersForItsOwnProviderKind()
    {
        Assert.Equal(AiProviderKind.OpenAi, Driver().ProviderKind);
    }

    private static OpenAiProviderDriver Driver(bool allowPrivateEgress = false, bool allowInsecureScheme = false)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("AiProviderAdmin");
        services.AddSingleton<OpenAiCompatibleRequestFactory>();
        services.AddSingleton<OpenAiCompatibleTransport>();
        var provider = services.BuildServiceProvider();

        return new OpenAiProviderDriver(
            provider.GetRequiredService<OpenAiCompatibleTransport>(),
            provider.GetRequiredService<IHttpClientFactory>(),
            allowPrivateEgress,
            allowInsecureScheme);
    }
}
