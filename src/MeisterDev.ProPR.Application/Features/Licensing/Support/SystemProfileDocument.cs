// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Support;

/// <summary>
///     Renders the stable components as one canonical document and hashes it.
///     <para>
///         The hash has to come out the same on every replica and on every later observation of an unchanged
///         installation, so the rendering fixes everything a serializer would otherwise be free to choose:
///     </para>
///     <list type="bullet">
///         <item>only the stable components are rendered; the volatile ones never reach it;</item>
///         <item>object members are written in ordinal order of their names;</item>
///         <item>an absent component is written as an explicit <c>null</c> rather than omitted, so absence is
///         part of the identity;</item>
///         <item>the host hashes are written as a set: duplicates removed, ordinal sort;</item>
///         <item>no whitespace anywhere, and the numeric members are written as JSON numbers.</item>
///     </list>
///     <para>
///         The hash is SHA-256 over the UTF-8 bytes of that rendering, in lower-case hexadecimal.
///     </para>
/// </summary>
internal static class SystemProfileDocument
{
    /// <summary>The canonical member names, which are also how a drift record names what changed.</summary>
    internal const string DatabaseNameMember = "databaseName";

    internal const string DatabaseOidMember = "databaseOid";
    internal const string IdentityCreatedAtMember = "identityCreatedAtUnixSeconds";
    internal const string PostgresSystemIdentifierMember = "postgresSystemIdentifier";
    internal const string ScmHostHashesMember = "scmHostHashes";

    /// <summary>Renders the stable components in their canonical form.</summary>
    /// <param name="components">The components to render.</param>
    /// <returns>The canonical JSON document.</returns>
    internal static string Render(SystemProfileStableComponents components)
    {
        ArgumentNullException.ThrowIfNull(components);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();

            WriteStringOrNull(writer, DatabaseNameMember, components.DatabaseName);
            WriteNumberOrNull(writer, DatabaseOidMember, components.DatabaseOid);
            WriteNumberOrNull(writer, IdentityCreatedAtMember, components.IdentityCreatedAtUnixSeconds);
            WriteStringOrNull(writer, PostgresSystemIdentifierMember, components.PostgresSystemIdentifier);
            WriteHostHashesOrNull(writer, components.ScmHostHashes);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Computes the profile hash of the stable components.</summary>
    /// <param name="components">The components to hash.</param>
    /// <returns>The hash, in lower-case hexadecimal.</returns>
    internal static string Hash(SystemProfileStableComponents components)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Render(components))));
    }

    /// <summary>
    ///     Names the components that differ between two observations, so a drift record says what moved rather
    ///     than only that something did.
    /// </summary>
    /// <param name="previous">What the installation reported before.</param>
    /// <param name="current">What it reports now.</param>
    /// <returns>The canonical member names that differ, in ordinal order.</returns>
    internal static IReadOnlyList<string> Diff(
        SystemProfileStableComponents previous,
        SystemProfileStableComponents current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var changed = new List<string>(5);

        if (!string.Equals(previous.DatabaseName, current.DatabaseName, StringComparison.Ordinal))
        {
            changed.Add(DatabaseNameMember);
        }

        if (previous.DatabaseOid != current.DatabaseOid)
        {
            changed.Add(DatabaseOidMember);
        }

        if (previous.IdentityCreatedAtUnixSeconds != current.IdentityCreatedAtUnixSeconds)
        {
            changed.Add(IdentityCreatedAtMember);
        }

        if (!string.Equals(previous.PostgresSystemIdentifier, current.PostgresSystemIdentifier, StringComparison.Ordinal))
        {
            changed.Add(PostgresSystemIdentifierMember);
        }

        if (!HostHashesMatch(previous.ScmHostHashes, current.ScmHostHashes))
        {
            changed.Add(ScmHostHashesMember);
        }

        return changed;
    }

    /// <summary>
    ///     The host hashes as the canonical rendering carries them: duplicates removed and ordinally sorted, or
    ///     <see langword="null" /> when the component is absent.
    /// </summary>
    private static List<string>? AsSet(IReadOnlyList<string>? hostHashes)
    {
        if (hostHashes is null)
        {
            return null;
        }

        var set = hostHashes.Distinct(StringComparer.Ordinal).ToList();
        set.Sort(StringComparer.Ordinal);

        return set;
    }

    private static bool HostHashesMatch(IReadOnlyList<string>? previous, IReadOnlyList<string>? current)
    {
        var previousSet = AsSet(previous);
        var currentSet = AsSet(current);

        if (previousSet is null || currentSet is null)
        {
            return previousSet is null && currentSet is null;
        }

        return previousSet.SequenceEqual(currentSet, StringComparer.Ordinal);
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string member, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(member);
            return;
        }

        writer.WriteString(member, value);
    }

    private static void WriteNumberOrNull(Utf8JsonWriter writer, string member, long? value)
    {
        if (value is not { } number)
        {
            writer.WriteNull(member);
            return;
        }

        writer.WriteNumber(member, number);
    }

    private static void WriteHostHashesOrNull(Utf8JsonWriter writer, IReadOnlyList<string>? hostHashes)
    {
        if (AsSet(hostHashes) is not { } set)
        {
            writer.WriteNull(ScmHostHashesMember);
            return;
        }

        writer.WriteStartArray(ScmHostHashesMember);
        foreach (var hostHash in set)
        {
            writer.WriteStringValue(hostHash);
        }

        writer.WriteEndArray();
    }

    /// <summary>
    ///     Reads a whole-second Unix instant back as a point in time, for a caller reporting the component
    ///     rather than hashing it.
    /// </summary>
    /// <param name="unixSeconds">The recorded value, or <see langword="null" /> when the component is absent.</param>
    /// <returns>The instant, or <see langword="null" />.</returns>
    internal static DateTimeOffset? ToInstant(long? unixSeconds) =>
        unixSeconds is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
