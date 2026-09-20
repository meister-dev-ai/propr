// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Amazon.Runtime;
using MeisterDev.Ai.Providers.BedrockAddIn;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Hosting;
using NSubstitute;

namespace MeisterDev.Ai.Providers.BedrockAddIn.Tests;

/// <summary>
///     Where the SDK sends a call, and which of the host's client pipelines it gets.
/// </summary>
public sealed class BedrockClientFactoryTests
{
    private const string ApiKey = "bedrock-api-key";

    // An interface VPC endpoint carries a 'vpce' label inside the AWS suffix. Reading one as a public service
    // host drops the address the operator entered and lets the SDK resolve the public regional endpoint, so
    // traffic put on PrivateLink to keep it off the internet goes over it with nothing saying so.
    [Fact]
    public void AVpcEndpointIsPinnedByUrlAndNotResolvedFromTheRegion()
    {
        // A VPC endpoint hostname names no region the resolver can read, so the operator states it as the query
        // parameter the family documents. That is the configuration this case is about.
        const string vpce = "https://vpce-0a1b2c3d-abcdefgh.bedrock-runtime.eu-central-1.vpce.amazonaws.com";

        var client = new BedrockClientFactory().CreateRuntimeClient(EndpointOn(vpce, region: "eu-central-1"));

        Assert.NotNull(client);
        Assert.Equal(vpce, client.Config.ServiceURL?.TrimEnd('/'));
        Assert.Equal("eu-central-1", client.Config.AuthenticationRegion);
    }

    [Fact]
    public void APublicRegionalHostIsResolvedFromTheRegion()
    {
        var client = new BedrockClientFactory()
            .CreateRuntimeClient(EndpointOn("https://bedrock-runtime.eu-central-1.amazonaws.com"));

        Assert.NotNull(client);
        Assert.Null(client.Config.ServiceURL);
        Assert.Equal("eu-central-1", client.Config.RegionEndpoint.SystemName);
    }

    // Model discovery is configuration-time work and not a call a review makes, and the purpose is what decides
    // the pipeline the host composes around the client.
    [Fact]
    public void TheControlPlaneClientAsksTheHostForAnAdminPipeline()
    {
        var http = Substitute.For<IProviderHttpClientFactory>();
        http.Create(Arg.Any<ProviderHttpPurpose>(), Arg.Any<IReadOnlyList<DelegatingHandler>?>())
            .Returns(new HttpClient());

        var control = new BedrockClientFactory()
            .CreateControlPlaneClient(EndpointOn("https://bedrock-runtime.eu-central-1.amazonaws.com", http));

        Assert.NotNull(control);
        control.Config.HttpClientFactory.CreateHttpClient(control.Config);

        http.Received().Create(ProviderHttpPurpose.Admin, Arg.Any<IReadOnlyList<DelegatingHandler>?>());
        http.DidNotReceive().Create(ProviderHttpPurpose.Runtime, Arg.Any<IReadOnlyList<DelegatingHandler>?>());
    }

    [Fact]
    public void TheRuntimeClientAsksTheHostForARuntimePipeline()
    {
        var http = Substitute.For<IProviderHttpClientFactory>();
        http.Create(Arg.Any<ProviderHttpPurpose>(), Arg.Any<IReadOnlyList<DelegatingHandler>?>())
            .Returns(new HttpClient());

        var runtime = new BedrockClientFactory()
            .CreateRuntimeClient(EndpointOn("https://bedrock-runtime.eu-central-1.amazonaws.com", http));

        Assert.NotNull(runtime);
        runtime.Config.HttpClientFactory.CreateHttpClient(runtime.Config);

        http.Received().Create(ProviderHttpPurpose.Runtime, Arg.Any<IReadOnlyList<DelegatingHandler>?>());
        http.DidNotReceive().Create(ProviderHttpPurpose.Admin, Arg.Any<IReadOnlyList<DelegatingHandler>?>());
    }

    private static ProviderEndpoint EndpointOn(
        string baseUrl,
        IProviderHttpClientFactory? http = null,
        string? region = null)
    {
        var factory = http ?? Substitute.For<IProviderHttpClientFactory>();
        var context = Substitute.For<IProviderConnectionContext>();
        context.Http.Returns(factory);

        return new ProviderEndpoint(
            BedrockProviderDriver.FamilyKey,
            baseUrl,
            BedrockProviderDriver.ApiKeyAuth)
        {
            Secret = ApiKey,
            HostContext = context,
            DefaultQueryParams = region is null
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = region },
        };
    }
}
