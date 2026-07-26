// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Amazon.Bedrock;
using Amazon.BedrockRuntime;
using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     Produces the AWS clients a Bedrock profile is served by.
/// </summary>
/// <remarks>
///     A seam rather than a direct construction because the AWS clients reach the network in their constructor's
///     shadow: without it, nothing about the driver could be exercised without an AWS account.
/// </remarks>
public interface IBedrockClientFactory
{
    /// <summary>Builds the inference client for an endpoint.</summary>
    /// <param name="endpoint">The stored provider endpoint.</param>
    /// <exception cref="InvalidOperationException">The endpoint names no region, or carries no access key.</exception>
    IAmazonBedrockRuntime CreateRuntimeClient(ProviderEndpoint endpoint);

    /// <summary>
    ///     Builds the control-plane client used to list the models an account can reach, or
    ///     <see langword="null" /> when the endpoint is not an AWS host and the control plane cannot be derived
    ///     from it.
    /// </summary>
    /// <param name="endpoint">The stored provider endpoint.</param>
    /// <exception cref="InvalidOperationException">The endpoint names no region, or carries no access key.</exception>
    IAmazonBedrock? CreateControlPlaneClient(ProviderEndpoint endpoint);
}
