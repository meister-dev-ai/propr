// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Amazon;
using Amazon.Bedrock;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.Ai.Providers.BedrockAddIn;

/// <summary>
///     Builds the AWS clients a Bedrock profile is served by, pinned to the region the profile names.
/// </summary>
/// <remarks>
///     Two decisions are made here rather than left to the SDK's defaults. Requests go out on the client the
///     host supplies for the endpoint, so AWS traffic carries the same connect-time address check as everything
///     else instead of leaving through a transport of the SDK's own. And the SDK's own retrying is turned off,
///     because the host's retry decorator already owns that decision — two independent retriers multiply into
///     attempts nobody budgeted for and a failure classification nobody can trace.
/// </remarks>
public sealed class BedrockClientFactory : IBedrockClientFactory
{
    /// <inheritdoc />
    public IAmazonBedrockRuntime? CreateRuntimeClient(ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (BedrockTransport.Factory(endpoint) is not { } http)
        {
            return null;
        }

        var config = new AmazonBedrockRuntimeConfig();
        Configure(config, endpoint, http, ProviderHttpPurpose.Runtime, pinServiceUrl: true);

        ApplyApiKey(endpoint, config);

        return new AmazonBedrockRuntimeClient(config);
    }

    /// <inheritdoc />
    public IAmazonBedrock? CreateControlPlaneClient(ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!IsPublicAwsServiceHost(endpoint.BaseUrl))
        {
            // A private or VPC inference endpoint says nothing about where its control plane lives, and guessing
            // would send an account's model list somewhere the operator never named.
            return null;
        }

        // A missing transport is refused rather than returned as null. Null here means the control plane cannot
        // be derived from the endpoint, which verification reports as an accepted configuration; a host that
        // supplied no transport is a different thing and has to be visible rather than read as a pass.
        var http = BedrockTransport.Factory(endpoint)
                   ?? throw new InvalidOperationException(BedrockTransport.NoClientFactory);

        var config = new AmazonBedrockConfig();
        Configure(config, endpoint, http, ProviderHttpPurpose.Admin, pinServiceUrl: false);

        ApplyApiKey(endpoint, config);

        return new AmazonBedrockClient(config);
    }

    // Puts the connection's API key on the config. The key is a bearer token rather than an AWS credential: the
    // SDK resolves it through the token provider and signs nothing, so the client is constructed without one.
    // The scheme is stated rather than left to be inferred, because a host that also holds ambient AWS
    // credentials would otherwise be free to sign the request instead and the key would go unused.
    private static void ApplyApiKey(ProviderEndpoint endpoint, ClientConfig config)
    {
        // The Smithy identifier for bearer authentication, written out rather than taken from the SDK: the
        // constant it defines lives under Amazon.Runtime.Credentials.Internal, and an internal namespace carries
        // no compatibility promise. The identifier itself is part of the Smithy specification and does not move.
        const string BearerAuthScheme = "smithy.api#httpBearerAuth";

        if (BedrockEndpointResolution.ResolveBearerToken(endpoint) is not { } token)
        {
            throw new InvalidOperationException("An AWS Bedrock connection needs an API key.");
        }

        config.AWSTokenProvider = new ServiceBearerStaticTokenProvider(token, null);
        config.AuthSchemePreference = [BearerAuthScheme];
    }

    // A public regional service host, which the SDK can resolve from the region alone. An interface VPC
    // endpoint carries a 'vpce' label inside the same suffix, as in
    // vpce-0a1b2c3d-abcdefgh.bedrock-runtime.eu-central-1.vpce.amazonaws.com, and reading one as public drops the
    // address the operator entered: the SDK then resolves the public regional endpoint instead, and traffic an
    // operator put on PrivateLink to keep off the internet goes over it with nothing saying so.
    private static bool IsPublicAwsServiceHost(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.TrimEnd('.');

        if (host.EndsWith(".vpce.amazonaws.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".vpce.amazonaws.com.cn", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".amazonaws.com.cn", StringComparison.OrdinalIgnoreCase);
    }

    private static void Configure(
        ClientConfig config,
        ProviderEndpoint endpoint,
        IProviderHttpClientFactory http,
        ProviderHttpPurpose purpose,
        bool pinServiceUrl)
    {
        // An endpoint naming a region twice and disagreeing with itself is refused. The query parameter used to
        // win, so a URL naming one region signed for another, and the operator had no way to see which one the
        // request went to.
        if (BedrockEndpointResolution.DescribeRegionConflict(endpoint) is { } conflict)
        {
            throw new InvalidOperationException(
                $"The endpoint host names region '{conflict.FromHost}' and the 'region' query parameter names "
                + $"'{conflict.FromParameter}'. Remove one of them so the connection names a single region.");
        }

        var region = BedrockEndpointResolution.ResolveRegion(endpoint)
                     ?? throw new InvalidOperationException(
                         "An AWS Bedrock connection must name its region, either in the endpoint host "
                         + "(https://bedrock-runtime.eu-central-1.amazonaws.com) or as a 'region' query parameter.");

        // ServiceURL and RegionEndpoint are alternatives to each other in the SDK, so a private or VPC endpoint
        // is pinned by URL and told which region to sign for; an AWS host resolves from the region alone.
        if (pinServiceUrl && !IsPublicAwsServiceHost(endpoint.BaseUrl))
        {
            config.ServiceURL = endpoint.BaseUrl;
            config.AuthenticationRegion = region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }

        config.MaxErrorRetry = 0;
        config.HttpClientFactory = new HostSuppliedHttpClientFactory(http, purpose);
    }

    /// <summary>Hands the AWS SDK the client the host supplied instead of one of its own.</summary>
    /// <param name="http">The host's client factory for this endpoint.</param>
    /// <param name="purpose">
    ///     What the calls on this client are for, which decides the pipeline the host composes. Model discovery
    ///     on the control plane is configuration-time work and not a call a review makes.
    /// </param>
    private sealed class HostSuppliedHttpClientFactory(IProviderHttpClientFactory http, ProviderHttpPurpose purpose)
        : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            return http.Create(purpose);
        }

        // The lifetime belongs to the factory that produced it: caching or disposing it here would either pin a
        // handler past its rotation or dispose one still in use elsewhere.
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
    }
}
