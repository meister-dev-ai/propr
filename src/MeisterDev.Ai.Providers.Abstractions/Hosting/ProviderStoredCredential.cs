// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using MeisterDev.Ai.Providers.Diagnostics;

namespace MeisterDev.Ai.Providers.Hosting;

/// <summary>
///     The credential stored against one connection, as the family that wrote it named its fields, together with
///     when it stops being usable.
/// </summary>
/// <remarks>
///     <para>
///         The expiry travels with the fields because it is what a family reads to decide whether to renew. Read
///         outside the lock it says whether renewal is worth starting; read again inside the lock it says whether
///         another caller already did it, and that lets the callers that lost an exchange take the winner's
///         credential instead of each starting one of their own.
///     </para>
///     <para>
///         <see cref="ExpiresAt" /> is null for a credential that carries no expiry, which is an operator-entered
///         key and never something written through a credential session: the session requires one.
///     </para>
/// </remarks>
public sealed record ProviderStoredCredential
{
    /// <summary>Initializes a new instance of the <see cref="ProviderStoredCredential" /> class.</summary>
    /// <param name="fields">The credential fields, by the names the family declared for its mode.</param>
    /// <param name="expiresAt">When the credential stops being usable, or null when it carries no expiry.</param>
    public ProviderStoredCredential(IReadOnlyDictionary<string, string> fields, DateTimeOffset? expiresAt)
    {
        ArgumentNullException.ThrowIfNull(fields);

        this.Fields = fields.ToImmutableDictionary(StringComparer.Ordinal);
        this.ExpiresAt = expiresAt;
    }

    /// <summary>The credential fields, by the names the family declared for its mode.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; }

    /// <summary>When the credential stops being usable, or null when it carries no expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>A connection with nothing stored against it.</summary>
    /// <remarks>
    ///     Immutable rather than a dictionary behind the interface. This is one instance for the process, handed
    ///     to every family that reads an empty credential, and a read-only interface over a dictionary can be
    ///     cast back to the dictionary and written through.
    /// </remarks>
    public static ProviderStoredCredential None { get; } =
        new(ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal), null);

    /// <summary>Whether anything is stored.</summary>
    public bool IsPresent => this.Fields.Count > 0;

    /// <summary>
    ///     Whether the credential is still usable <paramref name="margin" /> from now. A credential with no
    ///     expiry is not usable by this measure, because nothing says when it stops being one.
    /// </summary>
    /// <param name="now">The moment to measure from.</param>
    /// <param name="margin">
    ///     How far ahead to look. A call made now outlives the moment it was made, so a credential expiring inside
    ///     the margin is renewed before it is used rather than during it.
    /// </param>
    public bool IsUsableAt(DateTimeOffset now, TimeSpan margin)
    {
        // A negative margin would move the deadline later and report an expired credential as usable. The
        // margin exists to stop using one shortly before it expires, so it only ever shortens the window.
        ArgumentOutOfRangeException.ThrowIfLessThan(margin, TimeSpan.Zero);

        return this.IsPresent && this.ExpiresAt is { } expiry && expiry - margin > now;
    }

    /// <summary>Renders the credential as its field names and its expiry; see <see cref="SecretSafeRendering" />.</summary>
    public override string ToString()
    {
        return $"{nameof(ProviderStoredCredential)} {{ Fields = [{SecretSafeRendering.KeyNames(this.Fields)}], "
               + $"ExpiresAt = {this.ExpiresAt?.ToString("O") ?? "none"} }}";
    }
}
