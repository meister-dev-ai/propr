// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Egress;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Gives a source-control client the transport this installation permits it to reach the network with.</summary>
internal static class ScmProviderHttpClientBuilderExtensions
{
    /// <summary>
    ///     Puts the connect-time egress guard nearest the wire on <paramref name="builder" />.
    /// </summary>
    /// <param name="builder">The named client being registered.</param>
    /// <remarks>
    ///     A host name that resolved publicly when the connection was saved can be rebound to an internal address
    ///     afterwards, so the address is checked on every connection and not only when the URL is stored. The
    ///     posture is read from the container at the moment the client is built, so one installation setting
    ///     governs source-control traffic and AI traffic alike.
    ///     <para>
    ///         This sets the client's primary handler and must therefore be the only registration on the client
    ///         that does: a later one replaces the guard, and an earlier one is replaced by it together with the
    ///         proxy, certificate, cookie and decompression settings it carried. The three source-control client
    ///         registrations configure no other primary handler, so what each of them sends goes through the
    ///         guard. A client that needs transport settings of its own takes them here, inside the handler this
    ///         extension builds.
    ///     </para>
    /// </remarks>
    public static IHttpClientBuilder GuardEgress(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            GuardedEgressHttpHandler.Create(
                serviceProvider.GetRequiredService<EgressUrlPolicy>().AllowPrivateEgress,
                allowAutoRedirect: true));
    }
}
