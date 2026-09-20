// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Hosting;

/// <summary>Why a claim on a keyed entry did not succeed.</summary>
/// <remarks>
///     Each is a different thing for an operator to do, so they stay distinguishable rather than collapsing into
///     one refusal. An entry that was never written is a state value that did not come from this installation; an
///     expired one is a flow that took too long; one already consumed is a callback delivered twice, which a
///     browser does on its own; and one claimed by the wrong principal is a completion arriving for an
///     authorization a different administrator started.
/// </remarks>
public enum ProviderClaimRefusal
{
    /// <summary>The entry was claimed.</summary>
    None = 0,

    /// <summary>No entry is stored under that key.</summary>
    NoSuchEntry = 1,

    /// <summary>The entry is stored but its expiry has passed.</summary>
    Expired = 2,

    /// <summary>The entry was already consumed by an earlier claim.</summary>
    AlreadyConsumed = 3,

    /// <summary>The entry is bound to a different acting principal than the one claiming it.</summary>
    WrongPrincipal = 4,
}

/// <summary>The outcome of a conditional claim.</summary>
/// <param name="Refusal">Why the claim did not succeed, or <see cref="ProviderClaimRefusal.None" /> when it did.</param>
/// <param name="Values">The values stored under the key, when the claim succeeded; otherwise empty.</param>
public sealed record ProviderClaimOutcome(
    ProviderClaimRefusal Refusal,
    IReadOnlyDictionary<string, string> Values)
{
    /// <summary>An unsuccessful claim, carrying the reason and no values.</summary>
    /// <remarks>
    ///     None is not a reason. Passing it built an outcome that reports no values and answers true to
    ///     <see cref="Claimed" />, so a caller reading the property acted on an entry it never got.
    /// </remarks>
    /// <param name="refusal">Why the claim did not succeed.</param>
    public static ProviderClaimOutcome Refused(ProviderClaimRefusal refusal)
    {
        if (refusal == ProviderClaimRefusal.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(refusal),
                refusal,
                "A refusal states why the claim did not succeed. Use the claimed outcome for one that did.");
        }

        return new ProviderClaimOutcome(refusal, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>Whether the caller consumed the entry.</summary>
    public bool Claimed => this.Refusal == ProviderClaimRefusal.None;
}

/// <summary>
///     Short-lived named values a family keeps between the start of a flow and its completion, scoped to the
///     family and addressed by a key of its own choosing.
/// </summary>
/// <remarks>
///     <para>
///         What it exists for is an authorization handshake: the family writes the verifier it will need against
///         the opaque state value it sent the vendor, and reads it back when the vendor redirects. The redirect
///         carries only that state, so the entry is addressed by key and not by connection.
///     </para>
///     <para>
///         Three properties belong to the primitive rather than to each family's use of it. Every entry has an
///         expiry, which the host sweeps. Values are protected by the host, so a family stores named values and
///         never an encoded blob. And <see cref="ClaimAsync" /> consumes an entry in one statement rather than a
///         read followed by a delete, and that makes single use hold when a browser delivers the same
///         callback twice.
///     </para>
///     <para>
///         The acting-principal binding is written and checked by the host, from the administrator who started
///         the invocation. That keeps a completion arriving without a session attributable to the administrator
///         who authorized it, without a family ever holding an administrator identity.
///     </para>
/// </remarks>
public interface IProviderKeyedStore
{
    /// <summary>Stores named values under a key, to be claimed once before the expiry.</summary>
    /// <remarks>
    ///     A key that already holds an entry is overwritten, claimed or not. The key a family addresses an entry
    ///     by is a value it chose for one flow, such as the opaque state it sent the vendor, so a second write
    ///     under the same key is that flow starting again. The previous attempt's expiry and consumed mark say
    ///     nothing about this one, and refusing would leave a family unable to restart a flow it abandoned.
    /// </remarks>
    /// <param name="entryKey">The key to address the entry by; unique within the family.</param>
    /// <param name="values">The values to keep, protected by the host.</param>
    /// <param name="expiresAt">When the entry stops being claimable.</param>
    /// <param name="ct">Cancels the write.</param>
    Task WriteAsync(
        string entryKey,
        IReadOnlyDictionary<string, string> values,
        DateTimeOffset expiresAt,
        CancellationToken ct = default);

    /// <summary>
    ///     Consumes an entry that is unexpired, not already consumed, and bound to the acting principal if it is
    ///     bound at all, reporting which of those failed when it does not.
    /// </summary>
    /// <param name="entryKey">The key the entry was written under.</param>
    /// <param name="ct">Cancels the claim.</param>
    Task<ProviderClaimOutcome> ClaimAsync(string entryKey, CancellationToken ct = default);
}
