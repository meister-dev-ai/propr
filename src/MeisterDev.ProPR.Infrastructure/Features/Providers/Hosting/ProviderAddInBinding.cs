// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     The one connection a set of host primitives acts on, and what the host knows about the family serving it.
/// </summary>
/// <remarks>
///     Built by the host from a stored connection and the declaration of the family registered for it, and held
///     by the primitives handed to that family. The family never sees it and never supplies any part of it,
///     and that keeps every primitive acting on the connection the host chose.
/// </remarks>
/// <param name="ConnectionProfileId">The connection every primitive in this set acts on.</param>
/// <param name="ConnectionDisplayName">
///     What an operator calls the connection, used where a refusal has to name it — a resource held by another
///     connection of the same family.
/// </param>
/// <param name="AddInKey">The family's declared identity, which scopes its stored entries and its leases.</param>
/// <param name="ProviderIdentity">
///     The provider identity the connection carried when this was built, as it is stored on the row. A connection
///     can be repointed to another family while a client holds the primitives built over the old one, so the
///     credential paths read the stored identity back and refuse a handle whose binding no longer names the same
///     family.
/// </param>
/// <param name="AuthMode">The authentication mode stored against the connection.</param>
/// <param name="RequiredCapabilityKey">
///     The premium capability the family declared it needs, or null for a family that needs none. Checked
///     against the licence before a credential is handed over.
/// </param>
/// <param name="SupersededIdentities">
///     The identity spellings this family supersedes, which rows written before it declared its key still hold.
/// </param>
public sealed record ProviderAddInBinding(
    Guid ConnectionProfileId,
    string ConnectionDisplayName,
    string AddInKey,
    string ProviderIdentity,
    string AuthMode,
    string? RequiredCapabilityKey,
    IReadOnlyList<string>? SupersededIdentities = null)
{
    /// <summary>Whether a stored provider identity names the family this binding was built over.</summary>
    /// <remarks>
    ///     One family has more than one spelling: the key it declares and the keys it supersedes. Matching
    ///     ignores case, as every other read of a stored identity does.
    /// </remarks>
    /// <param name="storedIdentity">The identity as it is on the row.</param>
    public bool NamesThisFamily(string? storedIdentity)
    {
        return ProviderVocabulary.KeysEqual(storedIdentity, this.ProviderIdentity)
               || ProviderVocabulary.KeysEqual(storedIdentity, this.AddInKey)
               || (this.SupersededIdentities ?? []).Any(superseded => ProviderVocabulary.KeysEqual(superseded, storedIdentity));
    }
}
