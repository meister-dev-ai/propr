// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Enums;

/// <summary>
///     What a host knows about the state of a connection's stored credential.
/// </summary>
/// <remarks>
///     <para>
///         The value set is host-owned and closed. A provider family signals one of these members through the
///         host's health primitive, and the host decides what to do with it; a family cannot define a member of
///         its own, because a column that meant something different per family is one no host logic can key on
///         and no console can render.
///     </para>
///     <para>
///         Declared in the contract assembly rather than beside the host's other enumerations, because a family
///         references only the contract and has to name a member to signal one. Being a compiled type here closes
///         the question at the boundary: a family cannot signal a member that does not exist.
///     </para>
///     <para>
///         A family's signal is one input among several. A fresh verification outranks it, and it outranks a
///         classification derived from a failed call. It never substitutes for verification.
///     </para>
/// </remarks>
public enum AiCredentialHealth
{
    /// <summary>
    ///     Nothing is known about the credential. Zero so a value that was never set reads as unreported rather
    ///     than as a credential someone checked and found usable.
    /// </summary>
    Unreported = 0,

    /// <summary>The stored credential is usable as it stands.</summary>
    Healthy = 1,

    /// <summary>
    ///     The credential cannot be renewed without an operator going back to the provider. A refresh that the
    ///     provider refused, a grant that was revoked, and a stored value the host cannot read all land here.
    /// </summary>
    NeedsReauthorization = 2,

    /// <summary>
    ///     The provider has disabled the credential, so re-authorizing the same account changes nothing until
    ///     the provider-side state is fixed.
    /// </summary>
    Disabled = 3,
}
