// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Covers the runtime side of the private-egress opt-in: how the "allow private egress" flag is derived
///     from the environment and the <c>AI_ALLOW_PRIVATE_EGRESS</c> knob, and how the connect-time SSRF guard is
///     wired from that flag. The config-time probe-validation side lives in
///     <see cref="AiProviderDriverValidationTests" />.
/// </summary>
public sealed class PrivateEgressOptInTests
{
    [Fact]
    public void AllowPrivateEgress_DefaultsToBlocked_OutsideDevelopmentWithNoKnob()
    {
        Assert.False(InfrastructureServiceExtensions.AllowPrivateEgress(isDevelopment: false, BuildConfiguration()));
    }

    [Fact]
    public void AllowPrivateEgress_PermittedWhenOperatorOptsIn()
    {
        var configuration = BuildConfiguration(("AI_ALLOW_PRIVATE_EGRESS", "true"));

        Assert.True(InfrastructureServiceExtensions.AllowPrivateEgress(isDevelopment: false, configuration));
    }

    [Fact]
    public void AllowPrivateEgress_ExplicitFalseStaysBlocked()
    {
        var configuration = BuildConfiguration(("AI_ALLOW_PRIVATE_EGRESS", "false"));

        Assert.False(InfrastructureServiceExtensions.AllowPrivateEgress(isDevelopment: false, configuration));
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("1")]
    [InlineData("")]
    public void AllowPrivateEgress_NonBooleanKnobFallsBackToBlocked(string knobValue)
    {
        var configuration = BuildConfiguration(("AI_ALLOW_PRIVATE_EGRESS", knobValue));

        Assert.False(InfrastructureServiceExtensions.AllowPrivateEgress(isDevelopment: false, configuration));
    }

    [Fact]
    public void AllowPrivateEgress_DevelopmentPermitsRegardlessOfKnob()
    {
        Assert.True(InfrastructureServiceExtensions.AllowPrivateEgress(isDevelopment: true, BuildConfiguration()));
        Assert.True(
            InfrastructureServiceExtensions.AllowPrivateEgress(
                isDevelopment: true,
                BuildConfiguration(("AI_ALLOW_PRIVATE_EGRESS", "false"))));
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
    public void DiWiring_KnobOn_NonDevelopment_PermitsPrivateHttpsButKeepsHttps()
    {
        // End-to-end through the real service registration: with the operator opt-in set and a non-Development
        // environment, the wired-up OpenAI driver must permit a private https endpoint (proving the knob is
        // threaded) yet still reject plain http (proving the knob is NOT wired into the scheme relaxation). This
        // pins the single wiring decision — private egress uses the opt-in, but http stays Development-only.
        var configuration = BuildConfiguration(("AI_ALLOW_PRIVATE_EGRESS", "true"));
        var services = new ServiceCollection();
        services.AddInfrastructureSupport(
            configuration,
            new TestHostEnvironment("Production"),
            includeProviderOperationalServices: false);

        using var provider = services.BuildServiceProvider();
        var openAiDriver = provider.GetServices<IAiProviderDriver>()
            .Single(driver => driver.ProviderKind == AiProviderKind.OpenAi);

        Assert.Null(openAiDriver.ValidateProbeTarget(new AiProbeTarget("https://10.0.0.5/v1", AiAuthMode.ApiKey, true)));
        Assert.Contains(
            "https",
            openAiDriver.ValidateProbeTarget(new AiProbeTarget("http://10.0.0.5/v1", AiAuthMode.ApiKey, true)));
    }

    [Fact]
    public void DiWiring_KnobOff_NonDevelopment_BlocksPrivateHost()
    {
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddInfrastructureSupport(
            configuration,
            new TestHostEnvironment("Production"),
            includeProviderOperationalServices: false);

        using var provider = services.BuildServiceProvider();
        var openAiDriver = provider.GetServices<IAiProviderDriver>()
            .Single(driver => driver.ProviderKind == AiProviderKind.OpenAi);

        Assert.Contains(
            "private",
            openAiDriver.ValidateProbeTarget(new AiProbeTarget("https://10.0.0.5/v1", AiAuthMode.ApiKey, true)));
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
