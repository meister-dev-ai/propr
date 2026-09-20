// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Startup;

/// <summary>
///     A provider family is served by one driver. These tests cover what the host does when the composition
///     breaks that: it refuses to start, and says which family and which drivers are involved.
/// </summary>
public sealed class AiProviderCompositionStartupTests
{
    private const string CompiledInApiKey = "example/stand-in:ApiKey";

    private const string CompiledInChatCompletions = "example/stand-in:ChatCompletions";

    /// <summary>
    ///     The identities the composed host serves, each declared by an add-in in the built-in directory. Pinned
    ///     as literals because each one is what that family's connections are stored against.
    /// </summary>
    private static readonly string[] ShippedFamilies =
    [
        "meisterdev/azureOpenAi",
        "meisterdev/openAi",
        "meisterdev/liteLlm",
        "meisterdev/openAiCompatible",
        "meisterdev/anthropic",
        "meisterdev/awsBedrock",
        "meisterdev/googleVertex",
    ];

    /// <summary>A declaration the registry accepts, for a driver registered only to collide with another.</summary>
    private static readonly ProviderDeclaration StandInDeclaration = new()
    {
        Key = "example/stand-in",
        Label = "A stand-in family",
        Version = "1.0",
        ContractVersion = ProviderContract.Version,
        AuthModes = [new ProviderDeclaredAuthMode(CompiledInApiKey, [AiCredentialFieldSupport.ApiKey])],
        ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto, CompiledInChatCompletions]),
        ConformanceInputs = new ProviderConformanceInputs(CompiledInApiKey),
    };

    /// <remarks>
    ///     Two registrations rather than one. A single registration for a family displaces the add-in that serves
    ///     it — the loader records that as a duplicate and the host starts — so it takes two for the registry to
    ///     be left with a family and no basis for choosing between the drivers claiming it.
    /// </remarks>
    [Fact]
    public void ASecondDriverForAServedFamilyStopsTheHostStarting()
    {
        var first = StandIn();
        var second = StandIn();

        using var factory = new ApiHostFactory(services =>
        {
            services.AddSingleton(first);
            services.AddSingleton(second);
        });

        var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains(StandInDeclaration.Key, failure.Message, StringComparison.Ordinal);
        Assert.Contains(first.GetType().FullName!, failure.Message, StringComparison.Ordinal);
        Assert.Contains(second.GetType().FullName!, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The same host without the extra registrations. It starts, and serves each family an add-in declares,
    ///     which is also what makes the failure above attributable to the duplicate.
    /// </summary>
    [Fact]
    public void TheFamiliesTheAddInDirectoryDeclaresStartTheHost()
    {
        using var factory = new ApiHostFactory();

        _ = factory.CreateClient();

        var registry = factory.Services.GetRequiredService<IAiProviderDriverRegistry>();

        Assert.Equal(
            ShippedFamilies.Order(StringComparer.Ordinal),
            registry.RegisteredKinds.Order(StringComparer.Ordinal));
    }

    /// <summary>A driver that answers enough for the registry to index it, and nothing else.</summary>
    private static IAiProviderDriver StandIn()
    {
        var driver = Substitute.For<IAiProviderDriver>();
        driver.Declaration.Returns(StandInDeclaration);
        driver.SupportedAuthModes.Returns(StandInDeclaration.SupportedAuthModes);
        driver.CredentialFields.Returns(StandInDeclaration.CredentialFields);

        return driver;
    }

    /// <summary>
    ///     The API host as a test composes it: no database, no background workers, and whatever extra services
    ///     the test under way needs.
    /// </summary>
    private sealed class ApiHostFactory(Action<IServiceCollection>? configureServices = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("MEISTER_JWT_SECRET", "test-provider-composition-secret-32ch");

            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        }
    }
}
