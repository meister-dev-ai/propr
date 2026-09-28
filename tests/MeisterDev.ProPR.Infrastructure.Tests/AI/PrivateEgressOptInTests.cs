// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     Covers the runtime side of the private-egress opt-in: how the "allow private egress" flag is derived
///     from the environment and the <c>MEISTER_ALLOW_PRIVATE_EGRESS</c> knob, how the connect-time SSRF guard is
///     wired from that flag, and what posture the composed infrastructure states for a provider family to
///     apply. What a family does with that posture is covered in the family's own test project, and what an
///     operator meets when saving a connection against a private address is covered over the composed host.
/// </summary>
public sealed class PrivateEgressOptInTests
{
    /// <summary>The name the opt-in was first shipped under, still accepted so an existing installation keeps working.</summary>
    private const string LegacyOptIn = "AI_ALLOW_PRIVATE_EGRESS";

    [Fact]
    public void AllowPrivateEgress_DefaultsToBlocked_OutsideDevelopmentWithNoKnob()
    {
        Assert.False(InfrastructureServiceExtensions.AllowPrivateEgress(isDevelopment: false, BuildConfiguration()));
    }

    [Theory]
    [InlineData(EgressUrlPolicy.PrivateEgressOptIn)]
    [InlineData(LegacyOptIn)]
    public void AllowPrivateEgress_PermittedWhenOperatorOptsIn(string knob)
    {
        var configuration = BuildConfiguration((knob, "true"));

        Assert.True(InfrastructureServiceExtensions.AllowPrivateEgress(isDevelopment: false, configuration));
    }

    [Theory]
    [InlineData(EgressUrlPolicy.PrivateEgressOptIn)]
    [InlineData(LegacyOptIn)]
    public void AllowPrivateEgress_ExplicitFalseStaysBlocked(string knob)
    {
        var configuration = BuildConfiguration((knob, "false"));

        Assert.False(InfrastructureServiceExtensions.AllowPrivateEgress(isDevelopment: false, configuration));
    }

    [Fact]
    public void AllowPrivateEgress_CurrentNameWinsOverTheLegacyOne()
    {
        Assert.False(
            InfrastructureServiceExtensions.AllowPrivateEgress(
                isDevelopment: false,
                BuildConfiguration((EgressUrlPolicy.PrivateEgressOptIn, "false"), (LegacyOptIn, "true"))));
        Assert.True(
            InfrastructureServiceExtensions.AllowPrivateEgress(
                isDevelopment: false,
                BuildConfiguration((EgressUrlPolicy.PrivateEgressOptIn, "true"), (LegacyOptIn, "false"))));
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData(" ")]
    public void AllowPrivateEgress_NonBooleanKnobFallsBackToBlocked(string knobValue)
    {
        var configuration = BuildConfiguration((EgressUrlPolicy.PrivateEgressOptIn, knobValue));

        Assert.False(InfrastructureServiceExtensions.AllowPrivateEgress(isDevelopment: false, configuration));
    }

    // A typo in the setting the operator meant to control this with is answered by the safe default, not by a
    // setting they did not touch.
    [Fact]
    public void AllowPrivateEgress_NonBooleanCurrentKnobIsNotAnsweredByTheLegacyOne()
    {
        var configuration = BuildConfiguration((EgressUrlPolicy.PrivateEgressOptIn, "maybe"), (LegacyOptIn, "true"));

        Assert.False(InfrastructureServiceExtensions.AllowPrivateEgress(isDevelopment: false, configuration));
    }

    // An operator who set the current name meant to control this with it, so a value that says nothing is
    // answered by the safe default and not by a setting they did not touch.
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void AllowPrivateEgress_AnEmptyCurrentKnobIsNotAnsweredByTheLegacyOne(string knobValue)
    {
        var configuration = BuildConfiguration((EgressUrlPolicy.PrivateEgressOptIn, knobValue), (LegacyOptIn, "true"));

        Assert.False(InfrastructureServiceExtensions.AllowPrivateEgress(isDevelopment: false, configuration));
    }

    [Fact]
    public void AllowPrivateEgress_AbsentCurrentKnobIsAnsweredByTheLegacyOne()
    {
        var configuration = BuildConfiguration((LegacyOptIn, "true"));

        Assert.True(InfrastructureServiceExtensions.AllowPrivateEgress(isDevelopment: false, configuration));
    }

    [Fact]
    public void AllowPrivateEgress_DevelopmentPermitsRegardlessOfKnob()
    {
        Assert.True(InfrastructureServiceExtensions.AllowPrivateEgress(isDevelopment: true, BuildConfiguration()));
        Assert.True(
            InfrastructureServiceExtensions.AllowPrivateEgress(
                isDevelopment: true,
                BuildConfiguration((EgressUrlPolicy.PrivateEgressOptIn, "false"))));
    }

    [Fact]
    public void GuardedHandler_InstallsConnectGuard_WhenPrivateEgressBlocked()
    {
        using var handler = GuardedEgressHttpHandler.Create(allowPrivateEgress: false);

        Assert.NotNull(handler.ConnectCallback);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void GuardedHandler_OmitsConnectGuard_WhenPrivateEgressPermitted()
    {
        using var handler = GuardedEgressHttpHandler.Create(allowPrivateEgress: true);

        Assert.Null(handler.ConnectCallback);
        // Redirects stay disabled regardless so a 3xx cannot bounce a request to an internal target.
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void DiWiring_KnobOn_NonDevelopment_PermitsAPrivateAddressAndKeepsHttps()
    {
        // End-to-end through the real service registration: with the operator opt-in set and a non-Development
        // environment, the installation permits a private address (proving the knob is threaded) and still
        // requires https (proving the knob is NOT wired into the scheme relaxation). This pins the single wiring
        // decision — private egress uses the opt-in, but http stays Development-only.
        var policy = ResolveEgressPolicy(
            BuildConfiguration((EgressUrlPolicy.PrivateEgressOptIn, "true")),
            environmentName: "Production");

        Assert.True(policy.AllowPrivateEgress);
        Assert.False(policy.AllowInsecureScheme);
    }

    [Fact]
    public void DiWiring_KnobOff_NonDevelopment_PermitsNeither()
    {
        var policy = ResolveEgressPolicy(BuildConfiguration(), environmentName: "Production");

        Assert.False(policy.AllowPrivateEgress);
        Assert.False(policy.AllowInsecureScheme);
    }

    [Fact]
    public void DiWiring_Development_PermitsBoth()
    {
        var policy = ResolveEgressPolicy(BuildConfiguration(), environmentName: "Development");

        Assert.True(policy.AllowPrivateEgress);
        Assert.True(policy.AllowInsecureScheme);
    }

    /// <summary>The posture the composed infrastructure states for one configuration and environment.</summary>
    /// <param name="configuration">The configuration the host was started with.</param>
    /// <param name="environmentName">The environment the host was started in.</param>
    private static EgressUrlPolicy ResolveEgressPolicy(IConfiguration configuration, string environmentName)
    {
        var services = new ServiceCollection();
        services.AddInfrastructureSupport(
            configuration,
            new TestHostEnvironment(environmentName),
            includeProviderOperationalServices: false);

        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<EgressUrlPolicy>();
    }

    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] entries)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in entries)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = nameof(PrivateEgressOptInTests);

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
