// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net.Sockets;
using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Asks a driver to verify an endpoint or to list its models, and reports a call that got no answer as a failed
///     result.
/// </summary>
/// <remarks>
///     A family reports what the provider answered, and a call can end without an answer: the address does not
///     resolve, the egress rules of the installation refuse the connection, the host does not trust the certificate,
///     or the call times out. Without this, such a failure is an exception, and the caller answers an internal error
///     that names none of these causes. The exception is described the way
///     <see cref="DriverFailureMapper.Failed(Exception)" /> describes it. A cancellation the caller asked for is not a
///     failure of the provider and propagates.
/// </remarks>
public static class DriverCalls
{
    /// <summary>Verifies the endpoint, and reports a call that got no answer as a failed verification.</summary>
    public static async Task<ProviderVerificationResult> VerifyAsync(
        IAiProviderDriver driver,
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        try
        {
            return await driver.VerifyAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUnanswered(exception, ct))
        {
            return DriverFailureMapper.Failed(exception);
        }
    }

    /// <summary>Lists the models of the endpoint, and reports a call that got no answer as a failed discovery.</summary>
    public static async Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        IAiProviderDriver driver,
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        try
        {
            return await driver.DiscoverModelsAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUnanswered(exception, ct))
        {
            return DriverFailureMapper.DiscoveryFailed(exception);
        }
    }

    /// <summary>
    ///     Whether an exception means that the call reached no answer, or an answer the transport refused: a network,
    ///     TLS or protocol failure, a timeout, or a status that a client raised as an exception. A cancellation the
    ///     caller asked for is not one.
    /// </summary>
    /// <param name="exception">The exception the call threw.</param>
    /// <param name="ct">The cancellation of the caller.</param>
    public static bool IsUnanswered(Exception exception, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (ct.IsCancellationRequested)
        {
            return false;
        }

        return DriverFailureMapper.Unwind(exception).Any(cause => cause is HttpRequestException or IOException
            or SocketException or TimeoutException or OperationCanceledException);
    }
}
