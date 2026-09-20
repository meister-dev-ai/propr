// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Collections.Immutable;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;

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
///         That reading applies to a tenant that stated no entries, not to one whose entries no loaded family
///         claims. Such an entry is kept as an opaque key that matches no family, so the tenant stays restricted
///         and every family is refused. A restriction that lifted itself when its contents stopped resolving
///         would be a restriction an operator could not rely on, and a family can stop resolving through a
///         rename, a removal, or a host where its add-in was never installed.
///     </para>
///     <para>
///         The host list is the one that answers "where does our code go". A provider family says how the traffic
///         is shaped; the host says who receives it, and for a family reached at an operator-supplied base URL
///         the family alone constrains nothing at all.
///     </para>
/// </remarks>
public sealed record TenantProviderPolicy
{
    private readonly ImmutableArray<string> _allowedKinds;

    // Both lists are held as immutable values. They are published as IReadOnlyList, and a caller that could cast
    // one back to its backing list could empty it: clearing the entries no loaded family claims turns a policy
    // that refuses everything into one that permits everything, and clearing the hosts lifts the endpoint leg.
    private readonly ImmutableArray<string> _allowedEndpointHosts;
    private readonly ImmutableArray<string> _unresolvedProviderEntries;

    /// <summary>Initializes a new instance of the <see cref="TenantProviderPolicy" /> class.</summary>
    /// <param name="allowedKinds">
    ///     The permitted provider families, by identity key; empty means unrestricted. Compared the way two keys
    ///     are compared everywhere else, so case is folded and a repeated family is listed once.
    /// </param>
    /// <param name="allowedEndpointHosts">
    ///     The permitted endpoint hosts; empty means unrestricted. An entry matches a host exactly, or — written
    ///     with a leading dot, as <c>.openai.azure.com</c> — any subdomain of it, which is how a tenant permits a
    ///     vendor whose customers each get their own name.
    /// </param>
    /// <param name="unresolvedProviderEntries">
    ///     Permitted-family entries no loaded family claims. They match no family, and their presence makes the
    ///     policy restricted on the family leg even when no entry resolved.
    /// </param>
    public TenantProviderPolicy(
        IEnumerable<string> allowedKinds,
        IEnumerable<string>? allowedEndpointHosts = null,
        IEnumerable<string>? unresolvedProviderEntries = null)
    {
        ArgumentNullException.ThrowIfNull(allowedKinds);

        this._allowedKinds =
        [
            .. allowedKinds
                .Select(kind => kind.Trim())
                .Where(kind => kind.Length > 0)
                .Distinct(ProviderVocabulary.KeyComparer)
                .Order(StringComparer.Ordinal)
        ];

        this._allowedEndpointHosts = (allowedEndpointHosts ?? [])
            .Select(ProviderHostPattern.Normalize)
            .Where(host => host.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

        // A blank entry is kept, unlike a blank host: it is still an entry the tenant stored that no family
        // claims, so dropping it would restore the fail-open for a value written straight into the column.
        this._unresolvedProviderEntries = (unresolvedProviderEntries ?? [])
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
    }

    /// <summary>A policy that permits every provider family and every host, for tenants with no stated policy.</summary>
    public static TenantProviderPolicy Unrestricted { get; } = new([]);

    /// <summary>The permitted provider families, by identity key, in ordinal order; empty when unrestricted.</summary>
    public IReadOnlyList<string> AllowedKinds => this._allowedKinds;

    /// <summary>The permitted endpoint hosts, in the order stated; empty when unrestricted.</summary>
    public IReadOnlyList<string> AllowedEndpointHosts => this._allowedEndpointHosts;

    /// <summary>
    ///     The permitted-family entries no loaded family claims, in the order stored. An operator reads these to
    ///     find out which entry stopped a tenant from permitting anything, and names one to remove it.
    /// </summary>
    public IReadOnlyList<string> UnresolvedProviderEntries => this._unresolvedProviderEntries;

    /// <summary>Whether this tenant restricts which provider families may be used.</summary>
    public bool IsRestricted => this._allowedKinds.Length > 0 || this._unresolvedProviderEntries.Length > 0;

    /// <summary>Whether this tenant restricts which endpoint hosts may be reached.</summary>
    public bool RestrictsEndpoints => this._allowedEndpointHosts.Length > 0;

    /// <summary>Reads a tenant's stored policy columns into the rule object both enforcement and reads use.</summary>
    /// <param name="storedProviderIdentities">The stored permitted-family identities, as written to the column.</param>
    /// <param name="storedEndpointHosts">The stored permitted endpoint hosts, as written to the column.</param>
    /// <param name="registry">
    ///     The loaded families, which are what an entry is read against: an entry naming a family by its declared
    ///     identity key, or by a spelling that family supersedes, resolves to that family's key.
    /// </param>
    /// <returns>
    ///     <see cref="Unrestricted" /> when the tenant stored nothing on either leg; otherwise a policy carrying
    ///     the families that resolved and, as opaque keys, the entries no loaded family claims.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         One translation, shared by the enforcement path and the tenant's admin read, so an operator's view
    ///         cannot disagree with what is enforced. The discrimination that matters is between a tenant that
    ///         stored no entries, which is unrestricted, and one whose entries were all stored and did not
    ///         resolve, which is restricted and permits nothing.
    ///     </para>
    ///     <para>
    ///         Each resolved entry is held as the claiming family's declared key, which is the identity every
    ///         other stored location resolves to as well. Holding the spelling the entry happened to be written
    ///         under would leave an allow-list written before a family declared its key refusing that family's
    ///         connections, which now carry the key.
    ///     </para>
    /// </remarks>
    public static TenantProviderPolicy FromStored(
        IReadOnlyList<string>? storedProviderIdentities,
        IReadOnlyList<string>? storedEndpointHosts,
        IAiProviderDriverRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var identities = storedProviderIdentities ?? [];
        var hosts = storedEndpointHosts ?? [];

        if (identities.Count == 0 && hosts.Count == 0)
        {
            return Unrestricted;
        }

        var kinds = new List<string>(identities.Count);
        var unresolved = new List<string>();
        foreach (var identity in identities)
        {
            var resolution = registry.ResolveIdentity(identity);

            if (resolution.TryGetKey(out var key))
            {
                kinds.Add(key);
            }
            else
            {
                unresolved.Add(resolution.Name);
            }
        }

        return new TenantProviderPolicy(kinds, hosts, unresolved);
    }

    /// <summary>Whether <paramref name="providerKind" /> may be used under this policy.</summary>
    /// <param name="providerKind">The identity key of the provider family a profile uses.</param>
    /// <remarks>
    ///     Only the entries that resolved permit anything. An entry no loaded family claims names no family a
    ///     connection can be stored against, so matching one would permit whatever happened to be spelled the
    ///     same way rather than the family the tenant meant.
    /// </remarks>
    public bool IsAllowed(string? providerKind)
    {
        return !this.IsRestricted
               || this._allowedKinds.Any(allowed => ProviderVocabulary.KeysEqual(allowed, providerKind));
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

        return ProviderHostPattern.DoesAnyPatternMatchHost(this._allowedEndpointHosts, uri.Host);
    }

    /// <summary>
    ///     Whether every host <paramref name="reachedHostPattern" /> admits may be reached under this policy.
    /// </summary>
    /// <param name="reachedHostPattern">A host pattern a provider family declared it reaches.</param>
    /// <remarks>
    ///     A second question, asked of the same list. A declared pattern can be a leading-dot suffix, which is not
    ///     a URL, so <see cref="IsEndpointAllowed" /> would refuse it for being unparseable and every restricted
    ///     tenant would turn away every family declaring one. The question here is containment: an entry permits a
    ///     pattern when it names at least every host that pattern admits, which is why a bare-host entry never
    ///     permits a suffix pattern.
    /// </remarks>
    public bool IsPatternAllowed(string? reachedHostPattern)
    {
        return !this.RestrictsEndpoints
               || ProviderHostPattern.DoesAnyEntryCoverPattern(this._allowedEndpointHosts, reachedHostPattern);
    }

    /// <summary>
    ///     A user-facing reason for refusing <paramref name="providerKind" />, or <see langword="null" /> when it
    ///     is permitted. Phrased so an operator learns both what was refused and what is available instead,
    ///     rather than only that something was denied.
    /// </summary>
    /// <param name="providerKind">The identity key of the provider family a profile uses.</param>
    /// <remarks>
    ///     The entries no loaded family claims are part of the reason. A tenant whose entries all stopped
    ///     resolving permits nothing, and without naming them the refusal would state that nothing is permitted
    ///     without saying which entry has to be corrected.
    /// </remarks>
    public string? GetRefusalReason(string? providerKind)
    {
        if (this.IsAllowed(providerKind))
        {
            return null;
        }

        var permitted = this._allowedKinds.Length == 0
            ? "none"
            : string.Join(", ", this._allowedKinds);

        var unresolved = this._unresolvedProviderEntries.Length == 0
            ? string.Empty
            : "; entries no loaded provider family claims: "
              + string.Join(", ", this._unresolvedProviderEntries.Select(entry => $"'{entry}'"));

        return $"the '{providerKind}' provider is not on this tenant's permitted provider list "
               + $"(permitted: {permitted}{unresolved})";
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

    /// <summary>
    ///     A user-facing reason for refusing where a connection's traffic would go, or <see langword="null" />
    ///     when every destination it names is permitted.
    /// </summary>
    /// <param name="baseUrl">The endpoint the connection is configured against, where it has one.</param>
    /// <param name="reachedHostPatterns">The host patterns the connection's provider family declared it reaches.</param>
    /// <remarks>
    ///     <para>
    ///         Every member has to pass. A family reaches more than one host — a model endpoint and an
    ///         authorization endpoint, alongside whatever base URL an operator entered — and if one permitted
    ///         member were enough, permitting the model host would implicitly permit every other host the family
    ///         declares.
    ///     </para>
    ///     <para>
    ///         The operator-visible cost is that a tenant permitting one vendor host and nothing else refuses a
    ///         family that also reaches an authorization host until that host is permitted too. The refusal names
    ///         the member that failed, so an operator can read what to add.
    ///     </para>
    ///     <para>
    ///         The base URL is a member only where the connection has one: a family whose endpoint is fixed by its
    ///         vendor keeps its addresses in its declaration, and the patterns are what the restriction is checked
    ///         against for it. A connection carrying neither passes this check, because it names no destination.
    ///     </para>
    /// </remarks>
    public string? DescribeReachRefusal(string? baseUrl, IEnumerable<string>? reachedHostPatterns)
    {
        if (!this.RestrictsEndpoints)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(baseUrl) && this.DescribeEndpointRefusal(baseUrl) is { } endpointRefusal)
        {
            return endpointRefusal;
        }

        var declared = reachedHostPatterns?.ToList() ?? [];

        // A connection with no base URL and a family that names no host says nothing about where its traffic
        // goes, and this tenant has said where its traffic may go. The family leg is what this check relaxes
        // for a family reaching a fixed vendor host, and such a family names it; one that names nothing is not
        // that case.
        if (string.IsNullOrWhiteSpace(baseUrl) && declared.Count == 0)
        {
            return "this profile names no endpoint and its provider family names no host it reaches, so it "
                   + $"cannot be checked against this tenant's permitted endpoint list (permitted: "
                   + $"{string.Join(", ", this.AllowedEndpointHosts)})";
        }

        foreach (var pattern in declared)
        {
            if (!this.IsPatternAllowed(pattern))
            {
                return $"'{pattern}', a host this profile's provider family reaches, is not on this tenant's "
                       + $"permitted endpoint list (permitted: {string.Join(", ", this.AllowedEndpointHosts)})";
            }
        }

        return null;
    }
}
