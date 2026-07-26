// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.ProPR.Application.AI;

/// <summary>
///     What a tenant permits its clients to reach: which provider families, and which endpoint hosts. One rule
///     object, so the answer cannot differ between the place a configuration is written and the place a
///     credential is about to be used.
/// </summary>
/// <remarks>
///     <para>
///         An empty list means unrestricted rather than "nothing permitted". That is the only reading under
///         which a tenant that has never expressed a policy keeps working, and it makes the policy opt-in: a
///         tenant that wants data-residency or procurement limits states them, and a tenant that does not is
///         unaffected. The two lists are independent — a tenant can restrict families, hosts, both, or neither.
///     </para>
///     <para>
///         The host list is the one that answers "where does our code go". A provider family says how the traffic
///         is shaped; the host says who receives it, and for a family reached at an operator-supplied base URL
///         the family alone constrains nothing at all.
///     </para>
/// </remarks>
public sealed record TenantProviderPolicy
{
    private readonly HashSet<AiProviderKind> _allowedKinds;
    private readonly List<string> _allowedEndpointHosts;

    /// <summary>Initializes a new instance of the <see cref="TenantProviderPolicy" /> class.</summary>
    /// <param name="allowedKinds">The permitted provider families; empty means unrestricted.</param>
    /// <param name="allowedEndpointHosts">
    ///     The permitted endpoint hosts; empty means unrestricted. An entry matches a host exactly, or — written
    ///     with a leading dot, as <c>.openai.azure.com</c> — any subdomain of it, which is how a tenant permits a
    ///     vendor whose customers each get their own name.
    /// </param>
    public TenantProviderPolicy(
        IEnumerable<AiProviderKind> allowedKinds,
        IEnumerable<string>? allowedEndpointHosts = null)
    {
        ArgumentNullException.ThrowIfNull(allowedKinds);

        this._allowedKinds = [.. allowedKinds];
        this._allowedEndpointHosts = (allowedEndpointHosts ?? [])
            .Select(host => host.Trim().Trim('/').ToLowerInvariant())
            .Where(host => host.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A policy that permits every provider family and every host, for tenants with no stated policy.</summary>
    public static TenantProviderPolicy Unrestricted { get; } = new([]);

    /// <summary>The permitted provider families, in enum order; empty when unrestricted.</summary>
    public IReadOnlyList<AiProviderKind> AllowedKinds => [.. this._allowedKinds.Order()];

    /// <summary>The permitted endpoint hosts, in the order stated; empty when unrestricted.</summary>
    public IReadOnlyList<string> AllowedEndpointHosts => this._allowedEndpointHosts;

    /// <summary>Whether this tenant restricts which provider families may be used.</summary>
    public bool IsRestricted => this._allowedKinds.Count > 0;

    /// <summary>Whether this tenant restricts which endpoint hosts may be reached.</summary>
    public bool RestrictsEndpoints => this._allowedEndpointHosts.Count > 0;

    /// <summary>Whether <paramref name="providerKind" /> may be used under this policy.</summary>
    /// <param name="providerKind">The provider family a profile uses.</param>
    public bool IsAllowed(AiProviderKind providerKind)
    {
        return !this.IsRestricted || this._allowedKinds.Contains(providerKind);
    }

    /// <summary>Whether traffic may be sent to <paramref name="baseUrl" /> under this policy.</summary>
    /// <param name="baseUrl">The endpoint a profile is configured against.</param>
    public bool IsEndpointAllowed(string? baseUrl)
    {
        if (!this.RestrictsEndpoints)
        {
            return true;
        }

        // An unparseable base URL is refused rather than waved through: a policy that only constrains the URLs
        // it can read is not a policy.
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        return this._allowedEndpointHosts.Exists(allowed => allowed.StartsWith('.')
            ? host.EndsWith(allowed, StringComparison.Ordinal) || host == allowed.TrimStart('.')
            : host == allowed);
    }

    /// <summary>
    ///     A user-facing reason for refusing <paramref name="providerKind" />, or <see langword="null" /> when it
    ///     is permitted. Phrased so an operator learns both what was refused and what is available instead,
    ///     rather than only that something was denied.
    /// </summary>
    /// <param name="providerKind">The provider family a profile uses.</param>
    public string? DescribeRefusal(AiProviderKind providerKind)
    {
        if (this.IsAllowed(providerKind))
        {
            return null;
        }

        return $"the '{providerKind}' provider is not on this tenant's permitted provider list "
               + $"(permitted: {string.Join(", ", this.AllowedKinds)})";
    }

    /// <summary>
    ///     A user-facing reason for refusing <paramref name="baseUrl" />, or <see langword="null" /> when it is
    ///     permitted.
    /// </summary>
    /// <param name="baseUrl">The endpoint a profile is configured against.</param>
    public string? DescribeEndpointRefusal(string? baseUrl)
    {
        if (this.IsEndpointAllowed(baseUrl))
        {
            return null;
        }

        return $"'{baseUrl}' is not on this tenant's permitted endpoint list "
               + $"(permitted: {string.Join(", ", this.AllowedEndpointHosts)})";
    }
}
