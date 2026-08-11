// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Runner;

/// <summary>
///     The one secret this host holds, and where it lives while it holds it.
///     <para>
///         In memory only. Writing it down would put a reusable credential on the disk of a host that is
///         meant to be disposable, and the enrollment token exists precisely so a host can obtain one at
///         start rather than be delivered with one.
///     </para>
///     <para>
///         Mutable because a credential expires and is renewed in place. Every call reads it at the moment
///         it is sent, so a renewal takes effect on the next request rather than at the next restart.
///     </para>
/// </summary>
public sealed class RunnerCredentialStore(IOptions<RunnerHostOptions> options)
{
    private readonly Lock _gate = new();
    private string? _credential = options.Value.Credential;
    private DateTimeOffset? _expiresAt;

    /// <summary>The credential to present, or null on a host that has not enrolled yet.</summary>
    public string? Current
    {
        get
        {
            lock (this._gate)
            {
                return this._credential;
            }
        }
    }

    /// <summary>Whether this host already has a credential, however it came by one.</summary>
    public bool IsEnrolled => this.Current is not null;

    /// <summary>
    ///     Whether the credential is close enough to expiry to be worth renewing now. Renewed early on
    ///     purpose: renewing at expiry means a window where every call fails while the renewal races them.
    /// </summary>
    /// <param name="now">The current time.</param>
    public bool NeedsRenewal(DateTimeOffset now)
    {
        lock (this._gate)
        {
            return this._credential is not null
                   && this._expiresAt is { } expiry
                   && expiry - now <= TimeSpan.FromHours(1);
        }
    }

    /// <summary>Records a newly issued credential.</summary>
    /// <param name="credential">The credential.</param>
    /// <param name="expiresAt">When it must be renewed by, when the control plane said.</param>
    public void Set(string credential, DateTimeOffset? expiresAt)
    {
        lock (this._gate)
        {
            this._credential = credential;
            this._expiresAt = expiresAt;
        }
    }
}

/// <summary>
///     Puts the current credential on every outbound call.
///     <para>
///         A handler rather than a header fixed when the client was built, because the credential changes:
///         a host enrolls after its clients exist, and renews without restarting. A header captured at
///         construction would send the credential the host started with forever, which on a first start is
///         no credential at all.
///     </para>
/// </summary>
public sealed class RunnerCredentialHandler(RunnerCredentialStore credentials) : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Absent on the enrollment call itself, which is the one operation that cannot present one.
        if (credentials.Current is { } credential && !request.Headers.Contains(RunnerCredentialHeader))
        {
            request.Headers.Add(RunnerCredentialHeader, credential);
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>The header a runner presents its credential in.</summary>
    public const string RunnerCredentialHeader = "X-ProPR-Runner-Credential";
}
