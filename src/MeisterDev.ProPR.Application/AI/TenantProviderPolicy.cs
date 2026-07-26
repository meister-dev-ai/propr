// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.ProPR.Application.AI;

/// <summary>
///     Which provider families a tenant permits its clients to use. One rule object, so the answer cannot differ
///     between the place a configuration is written and the place a credential is about to be used.
/// </summary>
/// <remarks>
///     An empty allow-list means unrestricted rather than "nothing permitted". That is the only reading under
///     which a tenant that has never expressed a policy keeps working, and it makes the policy opt-in: a tenant
///     that wants data-residency or procurement limits states them, and a tenant that does not is unaffected.
/// </remarks>
public sealed record TenantProviderPolicy
{
    private readonly HashSet<AiProviderKind> _allowedKinds;

    /// <summary>Initializes a new instance of the <see cref="TenantProviderPolicy" /> class.</summary>
    /// <param name="allowedKinds">The permitted provider families; empty means unrestricted.</param>
    public TenantProviderPolicy(IEnumerable<AiProviderKind> allowedKinds)
    {
        ArgumentNullException.ThrowIfNull(allowedKinds);

        this._allowedKinds = [.. allowedKinds];
    }

    /// <summary>A policy that permits every provider family, used for tenants with no stated policy.</summary>
    public static TenantProviderPolicy Unrestricted { get; } = new([]);

    /// <summary>The permitted provider families, in enum order; empty when unrestricted.</summary>
    public IReadOnlyList<AiProviderKind> AllowedKinds => [.. this._allowedKinds.Order()];

    /// <summary>Whether this tenant has stated a policy at all.</summary>
    public bool IsRestricted => this._allowedKinds.Count > 0;

    /// <summary>Whether <paramref name="providerKind" /> may be used under this policy.</summary>
    /// <param name="providerKind">The provider family a profile uses.</param>
    public bool IsAllowed(AiProviderKind providerKind)
    {
        return !this.IsRestricted || this._allowedKinds.Contains(providerKind);
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
}
