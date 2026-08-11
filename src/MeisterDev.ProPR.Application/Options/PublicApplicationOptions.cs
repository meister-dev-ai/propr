// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Options;

/// <summary>
///     Where this installation is reachable from a browser, for the times ProPR has to name itself in text
///     it sends somewhere else.
/// </summary>
public sealed class PublicApplicationOptions
{
    /// <summary>
    ///     The scheme, host and port the user interface is served from, with no trailing slash, or
    ///     <see langword="null" /> when the installation has not been told its public address.
    ///     <para>
    ///         Derived from <c>MEISTER_PUBLIC_BASE_URL</c> rather than configured separately. That value names
    ///         the API and may carry a path prefix such as <c>/api</c>, while the user interface is served from
    ///         the root of the same host, so only its authority applies here.
    ///     </para>
    ///     <para>
    ///         Anything built from this reaches a person who may be outside ProPR, so a link is offered when
    ///         the address is known and left out when it is not. A guessed host would send a reader nowhere.
    ///     </para>
    /// </summary>
    public string? UiOrigin { get; set; }
}
