// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.AddIns;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     What an administrator approved is the assembly's statement, and the driver's declaration is what the host
///     enforces afterwards. These measure the one against the other.
/// </summary>
public sealed class ProviderAddInAgreementTests
{
    [Fact]
    public void AnAddInWhoseDriverAgreesWithItsAssemblyIsActivatable()
    {
        Assert.Null(
            ProviderAddInAgreement.Disagreement(
                Stated(hosts: ["chatgpt.com", "auth.openai.com"]),
                Loaded(hosts: ["chatgpt.com", "auth.openai.com"])));
    }

    // A family whose endpoints come from local configuration declares the hosts that configuration names, which
    // is a subset of what it may reach. Refusing that would refuse the family for a configuration the
    // administrator already decided on.
    [Fact]
    public void ADriverContactingFewerHostsThanItsAssemblyStatesIsInsideTheDecision()
    {
        Assert.Null(
            ProviderAddInAgreement.Disagreement(
                Stated(hosts: ["chatgpt.com", "auth.openai.com"]),
                Loaded(hosts: ["chatgpt.com"])));
    }

    [Fact]
    public void ADriverContactingAHostItsAssemblyDoesNotStateIsRefused()
    {
        var refusal = ProviderAddInAgreement.Disagreement(
            Stated(hosts: ["chatgpt.com"]),
            Loaded(hosts: ["chatgpt.com", "evil.example.com"]));

        Assert.NotNull(refusal);
        Assert.Contains("evil.example.com", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ADriverDeclaringAnIdentityOtherThanTheOneApprovedIsRefused()
    {
        var refusal = ProviderAddInAgreement.Disagreement(
            Stated(key: "meisterdev/openAiSubscription"),
            Loaded(key: "meisterdev/somethingElse"));

        Assert.NotNull(refusal);
        Assert.Contains("meisterdev/somethingElse", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ADriverNeedingACapabilityItsAssemblyDoesNotStateIsRefused()
    {
        var refusal = ProviderAddInAgreement.Disagreement(
            Stated(capability: null),
            Loaded(capability: "premium.providers"));

        Assert.NotNull(refusal);
        Assert.Contains("premium.providers", refusal, StringComparison.Ordinal);
    }

    private static DiscoveredProviderAddIn Stated(
        string key = "meisterdev/openAiSubscription",
        IReadOnlyList<string>? hosts = null,
        string? capability = null)
    {
        return new DiscoveredProviderAddIn(
            "/plugins/family/family.dll",
            "hash",
            ProviderAddInOrigin.External,
            key,
            "A family",
            "1.0",
            "1.0",
            hosts ?? ["chatgpt.com"],
            capability,
            "family",
            "1.0.0.0",
            null);
    }

    private static LoadedProviderAddIn Loaded(
        string key = "meisterdev/openAiSubscription",
        IReadOnlyList<string>? hosts = null,
        string? capability = null)
    {
        return new LoadedProviderAddIn(
            key,
            "A family",
            "1.0",
            "1.0",
            hosts ?? ["chatgpt.com"],
            capability,
            "/plugins/family/family.dll",
            "hash",
            ProviderAddInOrigin.External);
    }
}
