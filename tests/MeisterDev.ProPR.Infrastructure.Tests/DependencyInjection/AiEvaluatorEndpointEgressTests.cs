// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Egress;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MeisterDev.ProPR.Infrastructure.Tests.DependencyInjection;

/// <summary>
///     The address rule the instruction evaluator's endpoint is held to. The evaluator client sends
///     <c>AI_API_KEY</c> in a header and keeps the scheme the endpoint names, so an address this installation
///     refuses has to stop composition instead of reaching the network with that key on it.
/// </summary>
public sealed class AiEvaluatorEndpointEgressTests
{
    [Theory]
    [InlineData("http://evaluator.example.com")]
    [InlineData("https://127.0.0.1:8443")]
    [InlineData("https://169.254.169.254")]
    [InlineData("not-an-address")]
    public void AnEndpointTheInstallationRefusesStopsComposition(string endpoint)
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => Compose(endpoint, "Production"));

        Assert.Contains("AI_EVALUATOR_ENDPOINT", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnHttpsEndpointOnAPublicHostIsComposed()
    {
        using var provider = Compose("https://evaluator.example.com", "Production").BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredKeyedService<IChatClient>("evaluator"));
    }

    // Development relaxes the scheme and the address range, so a local evaluator stays reachable.
    [Fact]
    public void ADevelopmentHostComposesAPlainHttpLoopbackEndpoint()
    {
        using var provider = Compose("http://127.0.0.1:4000", "Development").BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredKeyedService<IChatClient>("evaluator"));
    }

    // The evaluator is registered only where both variables are set, so an endpoint alone reaches no rule.
    [Fact]
    public void AnEndpointWithoutADeploymentComposesNothingToRefuse()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AI_EVALUATOR_ENDPOINT"] = "http://evaluator.example.com" })
            .Build();

        services.AddInfrastructureSupport(
            configuration,
            new TestHostEnvironment("Production"),
            includeProviderOperationalServices: false);

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetKeyedService<IChatClient>("evaluator"));
        Assert.False(provider.GetRequiredService<EgressUrlPolicy>().AllowPrivateEgress);
    }

    private static ServiceCollection Compose(string endpoint, string environmentName)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AI_EVALUATOR_ENDPOINT"] = endpoint,
                    ["AI_EVALUATOR_DEPLOYMENT"] = "gpt-evaluator",
                })
            .Build();

        services.AddInfrastructureSupport(
            configuration,
            new TestHostEnvironment(environmentName),
            includeProviderOperationalServices: false);

        return services;
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = nameof(AiEvaluatorEndpointEgressTests);

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
