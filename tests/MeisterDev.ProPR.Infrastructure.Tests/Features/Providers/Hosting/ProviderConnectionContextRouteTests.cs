// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     How a provider family's driver comes to hold what the host allows it to do against one connection.
/// </summary>
/// <remarks>
///     <para>
///         The primitives are bound to one connection and a driver serves every connection of its family from
///         one instance, so the handle rides on the endpoint the host hands the driver for that connection. A
///         driver never receives a connection identifier it could pass a different value for.
///     </para>
///     <para>
///         The client the driver builds keeps what it was handed, which is longer than the work that built it: a
///         review builds one client and calls it for as long as the review runs.
///     </para>
/// </remarks>
public sealed class ProviderConnectionContextRouteTests
{
    // The chokepoint is where every review path meets, so this is where a driver is given the handle. Without
    // it a driver has no route to the network, no route to its credential, and no way to renew one.
    [Fact]
    public void TheChokepointHandsTheDriverWhatTheHostAllowsAgainstTheConnectionItIsBuildingFor()
    {
        var driver = new RecordingDriver();
        var connection = Connection();
        var expected = Substitute.For<IProviderConnectionContext>();

        var contexts = Substitute.For<IProviderConnectionContextFactory>();
        contexts.ForConnection(connection).Returns(expected);

        var runtime = Factory(driver, contexts).CreateChatRuntime(
            connection,
            connection.ConfiguredModels[0],
            connection.PurposeBindings[0]);

        Assert.NotNull(runtime);
        Assert.Same(expected, driver.SawOnChatClient);
        Assert.Same(expected, driver.SawOnCapabilities);
    }

    // A composition with no route to build one still builds runtimes: the built-in families reach the network
    // through the host's own clients and need nothing from the primitives.
    [Fact]
    public void AChokepointWithNoRouteToTheHostPrimitivesStillBuildsARuntime()
    {
        var driver = new RecordingDriver();
        var connection = Connection();

        var runtime = Factory(driver, hostContexts: null).CreateChatRuntime(
            connection,
            connection.ConfiguredModels[0],
            connection.PurposeBindings[0]);

        Assert.NotNull(runtime);
        Assert.Null(driver.SawOnChatClient);
    }

    // The handle is a live route into the host and means nothing outside the process that made it, so it is not
    // part of what an endpoint serializes, and neither is what it reaches.
    [Fact]
    public void TheHandleIsNotPartOfWhatAnEndpointSerializes()
    {
        var endpoint = Connection().ToProviderEndpoint(Substitute.For<IProviderConnectionContext>());

        var serialized = System.Text.Json.JsonSerializer.Serialize(endpoint);

        Assert.DoesNotContain("HostContext", serialized, StringComparison.OrdinalIgnoreCase);
    }

    private static AiRuntimeFactory Factory(
        IAiProviderDriver driver,
        IProviderConnectionContextFactory? hostContexts)
    {
        var registry = Substitute.For<IAiProviderDriverRegistry>();
        registry.IsRegistered("meisterdev/openAiCompatible").Returns(true);
        registry.GetRequired("meisterdev/openAiCompatible").Returns(driver);

        return new AiRuntimeFactory(registry, hostContexts: hostContexts);
    }

    private static AiConnectionDto Connection()
    {
        var modelId = Guid.NewGuid();

        return new AiConnectionDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "A connection",
            "meisterdev/openAiCompatible",
            "https://api.example.com/v1",
            "meisterdev/openAiCompatible:ApiKey",
            AiDiscoveryMode.ManualOnly,
            true,
            [
                new AiConfiguredModelDto(
                    modelId,
                    "example-model",
                    "Example model",
                    [AiOperationKind.Chat],
                    ["meisterdev/openAiCompatible:ChatCompletions"],
                    null,
                    null,
                    null,
                    false,
                    false,
                    AiConfiguredModelSource.Manual,
                    DateTimeOffset.UtcNow),
            ],
            [
                new AiPurposeBindingDto(
                    Guid.NewGuid(),
                    AiPurpose.ReviewDefault,
                    modelId,
                    "example-model",
                    "meisterdev/openAiCompatible:ChatCompletions",
                    true),
            ],
            new AiVerificationResultDto(
                AiVerificationStatus.Verified,
                null,
                "verified",
                null,
                DateTimeOffset.UtcNow,
                []),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    /// <summary>A driver that records what the host put on the endpoint it was handed.</summary>
    private sealed class RecordingDriver : IAiProviderDriver
    {
        public IProviderConnectionContext? SawOnChatClient { get; private set; }

        public IProviderConnectionContext? SawOnCapabilities { get; private set; }

        public Ai.Providers.Declaration.ProviderDeclaration Declaration =>
            new Ai.Providers.ExampleAddIn.ExampleProviderDriver().Declaration;

        public string? ValidateProbeTarget(AiProbeTarget target)
        {
            return null;
        }

        public Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
            ProviderEndpoint endpoint,
            CancellationToken ct = default)
        {
            return Task.FromResult(new ProviderModelDiscoveryResult("succeeded", true, [], []));
        }

        public Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
        {
            return Task.FromResult(DriverFailureMapper.Verified("verified"));
        }

        public IChatClient CreateChatClient(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode)
        {
            this.SawOnChatClient = endpoint.HostContext;
            return Substitute.For<IChatClient>();
        }

        public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode)
        {
            this.SawOnCapabilities = endpoint.HostContext;
            return ProviderRuntimeCapabilities.None;
        }

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode,
            int dimensions)
        {
            throw new InvalidOperationException("This driver serves no embedding models.");
        }
    }
}
