// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.Ai.Providers.Tests.Conformance;

/// <summary>
///     The behaviour every provider driver owes the rest of the system, asserted once and run against all of them.
/// </summary>
/// <remarks>
///     <para>
///         Written as one parameterised suite rather than a set of per-driver tests on purpose. The property that
///         matters is that the drivers behave the <em>same</em> at the seam, and per-driver tests cannot show
///         that: each would assert its own driver's behaviour and none would notice the two drifting apart. A
///         shared suite turns seam parity from a claim into a measurement.
///     </para>
///     <para>
///         Adding a provider means adding a line to <see cref="Drivers" />. A driver that cannot pass this is not
///         shippable — the review loop calls every driver through the same seam and has no way to special-case one.
///     </para>
/// </remarks>
public sealed class DriverConformanceTests
{
    public static TheoryData<DriverConformanceFixture> Drivers()
    {
        return new TheoryData<DriverConformanceFixture>
        {
            new DriverConformanceFixture(
                "AzureOpenAi",
                () => new AzureOpenAiProviderDriver(),
                "https://contoso.openai.azure.com/",
                // Azure's driver is the one that insists on an Azure host, so a plain vendor URL is its refusal.
                "https://api.openai.com/v1",
                AiAuthMode.ApiKey),
            new DriverConformanceFixture(
                "OpenAi",
                () => Build((transport, factory) => new OpenAiProviderDriver(transport, factory, false, false)),
                "https://api.openai.com/v1",
                "https://contoso.openai.azure.com/"),
            new DriverConformanceFixture(
                "LiteLlm",
                () => Build((transport, factory) => new LiteLlmProviderDriver(transport, factory, false, false)),
                "https://gateway.example.com/v1",
                null),
            new DriverConformanceFixture(
                "Anthropic",
                () => Build((transport, factory) => new AnthropicProviderDriver(transport, factory, false, false)),
                "https://api.anthropic.com/v1",
                null,
                AiAuthMode.XApiKey),
            new DriverConformanceFixture(
                "OpenAiCompatible",
                () => Build((transport, factory) => new OpenAiCompatibleProviderDriver(transport, factory, false, false)),
                "https://opencode.ai/zen/v1",
                null),
        };
    }

    [Theory]
    [MemberData(nameof(Drivers))]
    public void ADriverAnswersForExactlyOneProviderFamily(DriverConformanceFixture fixture)
    {
        var driver = fixture.Create();

        // The registry indexes by this, so a driver that answered for none or changed its mind between calls
        // would be unreachable or would shadow another.
        Assert.Equal(driver.ProviderKind, fixture.Create().ProviderKind);
        Assert.True(Enum.IsDefined(driver.ProviderKind));
    }

    // Auto is what a binding says when it has no opinion, which is the common case, so a driver that cannot serve
    // it can never be selected by default.
    [Theory]
    [MemberData(nameof(Drivers))]
    public void ADriverSpeaksAtLeastOneShapeAndAlwaysSpeaksAuto(DriverConformanceFixture fixture)
    {
        var supported = fixture.Create().SupportedProtocolModes;

        Assert.NotEmpty(supported);
        Assert.Contains(AiProtocolMode.Auto, supported);
        Assert.Equal(supported.Distinct().Count(), supported.Count);
    }

    [Theory]
    [MemberData(nameof(Drivers))]
    public void ADriverRefusesAShapeItDoesNotSpeak(DriverConformanceFixture fixture)
    {
        var driver = fixture.Create();
        var unspeakable = Enum.GetValues<AiProtocolMode>().Except(driver.SupportedProtocolModes).ToList();
        Assert.NotEmpty(unspeakable);

        var failure = Assert.Throws<InvalidOperationException>(() => driver.CreateChatClient(Endpoint(fixture), Model(), unspeakable[0]));

        Assert.Contains(unspeakable[0].ToString(), failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Drivers))]
    public void ADriverAcceptsAPublicEndpointItIsMeantToServe(DriverConformanceFixture fixture)
    {
        var target = new AiProbeTarget(fixture.AcceptableBaseUrl, fixture.CredentialAuthMode, HasApiKey: true);

        Assert.Null(fixture.Create().ValidateProbeTarget(target));
    }

    [Theory]
    [MemberData(nameof(Drivers))]
    public void ADriverRefusesAnEndpointOutsideItsOwnRules(DriverConformanceFixture fixture)
    {
        if (fixture.RejectedBaseUrl is null)
        {
            // A driver that accepts any host has nothing to assert here; the egress cases below still apply.
            return;
        }

        var target = new AiProbeTarget(fixture.RejectedBaseUrl, fixture.CredentialAuthMode, HasApiKey: true);

        Assert.NotNull(fixture.Create().ValidateProbeTarget(target));
    }

    // Every driver is reached at an operator-supplied URL, so every driver has to be the one that refuses to be
    // pointed inward. One driver forgetting this is an SSRF hole in the whole product.
    [Theory]
    [MemberData(nameof(Drivers))]
    public void ADriverRefusesAPrivateAddressWhenPrivateEgressIsForbidden(DriverConformanceFixture fixture)
    {
        var driver = fixture.Create();

        foreach (var blocked in new[] { "https://127.0.0.1/v1", "https://169.254.169.254/latest/meta-data/", "https://10.0.0.5/v1" })
        {
            Assert.NotNull(driver.ValidateProbeTarget(new AiProbeTarget(blocked, fixture.CredentialAuthMode, HasApiKey: true)));
        }
    }

    [Theory]
    [MemberData(nameof(Drivers))]
    public void ADriverRefusesPlainHttpWhenTheInsecureSchemeIsForbidden(DriverConformanceFixture fixture)
    {
        var insecure = fixture.AcceptableBaseUrl.Replace("https://", "http://", StringComparison.Ordinal);

        Assert.NotNull(fixture.Create().ValidateProbeTarget(new AiProbeTarget(insecure, fixture.CredentialAuthMode, HasApiKey: true)));
    }

    // Refusing at configuration time is the difference between an operator seeing the problem and a review
    // failing on its first call with a provider-worded rejection.
    [Theory]
    [MemberData(nameof(Drivers))]
    public void ADriverRefusesAMissingCredential(DriverConformanceFixture fixture)
    {
        var target = new AiProbeTarget(fixture.AcceptableBaseUrl, fixture.CredentialAuthMode, HasApiKey: false);

        Assert.NotNull(fixture.Create().ValidateProbeTarget(target));
    }

    [Theory]
    [MemberData(nameof(Drivers))]
    public void ADriverBuildsAChatClientForEveryShapeItClaims(DriverConformanceFixture fixture)
    {
        var driver = fixture.Create();

        foreach (var mode in driver.SupportedProtocolModes.Where(mode => mode != AiProtocolMode.Embeddings))
        {
            using var client = driver.CreateChatClient(Endpoint(fixture), Model(), mode);
            Assert.NotNull(client);
        }
    }

    [Theory]
    [MemberData(nameof(Drivers))]
    public void ADriverDescribesItsRuntimeCapabilitiesWithoutThrowing(DriverConformanceFixture fixture)
    {
        var driver = fixture.Create();

        var capabilities = driver.GetChatRuntimeCapabilities(Endpoint(fixture), Model(), AiProtocolMode.Auto);

        Assert.NotNull(capabilities);
    }

    // Retry acts on the classification, not on an exception type, so a driver that classified differently would
    // quietly retry where its neighbour gives up — or spend a budget repeating a call that can never succeed.
    [Theory]
    [MemberData(nameof(Drivers))]
    public void ADriverClassifiesTheFailuresRetryDependsOn(DriverConformanceFixture fixture)
    {
        var driver = fixture.Create();

        Assert.True(driver.ClassifyRuntimeFailure(Http(429)).IsTransient);
        Assert.True(driver.ClassifyRuntimeFailure(Http(503)).IsTransient);
        Assert.False(driver.ClassifyRuntimeFailure(Http(401)).IsTransient);
        Assert.False(driver.ClassifyRuntimeFailure(Http(400)).IsTransient);
        Assert.True(driver.ClassifyRuntimeFailure(new HttpRequestException(HttpRequestError.ConnectionError, "down")).IsTransient);
    }

    private static Exception Http(int status)
    {
        return new HttpRequestException("provider said no", null, (System.Net.HttpStatusCode)status);
    }

    private static ProviderEndpoint Endpoint(DriverConformanceFixture fixture)
    {
        return new ProviderEndpoint(
            fixture.Create().ProviderKind,
            fixture.AcceptableBaseUrl,
            fixture.CredentialAuthMode,
            "conformance-key");
    }

    private static ProviderModelDescriptor Model()
    {
        return new ProviderModelDescriptor(
            Guid.NewGuid(),
            "conformance-model",
            [AiProtocolMode.Auto, AiProtocolMode.ChatCompletions]);
    }

    private static IAiProviderDriver Build(Func<OpenAiCompatibleTransport, IHttpClientFactory, IAiProviderDriver> create)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("AiProbe");
        services.AddHttpClient("AiProviderAdmin");
        services.AddHttpClient("AiProviderRuntime");
        services.AddSingleton<OpenAiCompatibleRequestFactory>();
        services.AddSingleton<OpenAiCompatibleTransport>();
        var provider = services.BuildServiceProvider();

        return create(
            provider.GetRequiredService<OpenAiCompatibleTransport>(),
            provider.GetRequiredService<IHttpClientFactory>());
    }
}
