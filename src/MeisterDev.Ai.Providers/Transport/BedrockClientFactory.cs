// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Amazon;
using Amazon.Bedrock;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     Builds the AWS clients a Bedrock profile is served by, pinned to the region the profile names.
/// </summary>
/// <remarks>
///     Two decisions are made here rather than left to the SDK's defaults. Requests go out through the
///     egress-guarded <see cref="HttpClient" /> the rest of the system uses, so AWS traffic is held to the same
///     address policy as everything else instead of quietly bypassing it. And the SDK's own retrying is turned
///     off, because the shared retry decorator already owns that decision — two independent retriers multiply
///     into attempts nobody budgeted for and a failure classification nobody can trace.
/// </remarks>
/// <param name="httpClientFactory">The factory producing the egress-guarded client.</param>
public sealed class BedrockClientFactory(IHttpClientFactory httpClientFactory) : IBedrockClientFactory
{
    private const string RuntimeClientName = "AiProviderRuntime";

    /// <inheritdoc />
    public IAmazonBedrockRuntime CreateRuntimeClient(ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var config = new AmazonBedrockRuntimeConfig();
        this.Configure(config, endpoint, pinServiceUrl: true);

        return new AmazonBedrockRuntimeClient(BedrockEndpointResolution.ResolveCredentials(endpoint), config);
    }

    /// <inheritdoc />
    public IAmazonBedrock? CreateControlPlaneClient(ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!IsAwsHost(endpoint.BaseUrl))
        {
            // A private or VPC inference endpoint says nothing about where its control plane lives, and guessing
            // would send an account's model list somewhere the operator never named.
            return null;
        }

        var config = new AmazonBedrockConfig();
        this.Configure(config, endpoint, pinServiceUrl: false);

        return new AmazonBedrockClient(BedrockEndpointResolution.ResolveCredentials(endpoint), config);
    }

    private static bool IsAwsHost(string baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
               && (uri.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
                   || uri.Host.EndsWith(".amazonaws.com.cn", StringComparison.OrdinalIgnoreCase));
    }

    private void Configure(ClientConfig config, ProviderEndpoint endpoint, bool pinServiceUrl)
    {
        var region = BedrockEndpointResolution.ResolveRegion(endpoint)
                     ?? throw new InvalidOperationException(
                         "An AWS Bedrock connection must name its region, either in the endpoint host "
                         + "(https://bedrock-runtime.eu-central-1.amazonaws.com) or as a 'region' query parameter.");

        // ServiceURL and RegionEndpoint are alternatives to each other in the SDK, so a private or VPC endpoint
        // is pinned by URL and told which region to sign for; an AWS host resolves from the region alone.
        if (pinServiceUrl && !IsAwsHost(endpoint.BaseUrl))
        {
            config.ServiceURL = endpoint.BaseUrl;
            config.AuthenticationRegion = region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }

        config.MaxErrorRetry = 0;
        config.HttpClientFactory = new GuardedHttpClientFactory(httpClientFactory);
    }

    /// <summary>Hands the AWS SDK the egress-guarded client instead of one of its own.</summary>
    private sealed class GuardedHttpClientFactory(IHttpClientFactory httpClientFactory) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            return httpClientFactory.CreateClient(RuntimeClientName);
        }

        // The lifetime belongs to the factory that produced it: caching or disposing it here would either pin a
        // handler past its rotation or dispose one still in use elsewhere.
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
    }
}
