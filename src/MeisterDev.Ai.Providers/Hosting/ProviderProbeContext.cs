// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Hosting;

/// <summary>
///     What a provider family may do while answering for values an operator has typed and not yet stored.
/// </summary>
/// <remarks>
///     The transport and nothing else. Discovery and verification are calls to the endpoint the operator entered,
///     and a family reaches the network only through the host's client factory, so a probe without one is a probe
///     no family can make. The other four primitives act on a stored connection, and there is none here: each
///     refuses rather than returning a stand-in that would act on nothing and report success.
/// </remarks>
/// <param name="http">The host's pipelines, the same ones a stored connection's calls leave through.</param>
public sealed class ProviderProbeContext(IProviderHttpClientFactory http) : IProviderConnectionContext
{
    /// <summary>What a refusal says, once, because all four say the same thing.</summary>
    private const string NoConnection =
        "This call is a configuration-time probe over values that have not been stored, so there is no connection "
        + "to act on. Save the connection first.";

    /// <inheritdoc />
    public IProviderHttpClientFactory Http { get; } = http;

    /// <inheritdoc />
    public IProviderCredentialSessions Credentials => throw new ProviderRequestRejectedException(NoConnection);

    /// <inheritdoc />
    public IProviderLeases Leases => throw new ProviderRequestRejectedException(NoConnection);

    /// <inheritdoc />
    public IProviderKeyedStore Store => throw new ProviderRequestRejectedException(NoConnection);

    /// <inheritdoc />
    public IProviderHealthSignal Health => throw new ProviderRequestRejectedException(NoConnection);
}
