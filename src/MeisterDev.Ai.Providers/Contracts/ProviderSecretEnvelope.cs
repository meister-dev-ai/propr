// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Diagnostics;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     The internal shape of a stored provider credential: the family that wrote it, the authentication mode it
///     was written for, and the named fields that mode needs. One credential is one opaque blob to whatever
///     stores it, and this is the only thing that knows what is inside.
/// </summary>
/// <remarks>
///     <para>
///         An API key is one string, and a credential a family declares need not be: an access key paired with a
///         secret and a session token, a client id beside a client secret. Without a defined shape, each family
///         would invent its own encoding inside the same opaque column, and the first one to need two fields
///         would either add a column or pack them with a separator nobody else knows about.
///     </para>
///     <para>
///         The shape is keyed by the writing family's identity key rather than by the authentication mode,
///         because a mode name does not name one payload shape. <c>ApiKey</c> already keys three incompatible
///         shapes: the Bedrock guidance tells an operator to pack an access key, a secret and an optional session
///         token into the single key field, and the Vertex guidance a service-account document, both carried
///         through the same field. Two families can declare the same mode name with different meanings, and a
///         family reading another family's envelope would decode against the wrong shape.
///     </para>
///     <para>
///         The envelope is versioned so a shape can change without guessing at what an old row means, and it
///         tolerates a bare string, which rows written before it existed contain. An envelope that
///         records no identity key is read by whichever family holds the row, for the same reason: it was
///         written before the key was recorded, and refusing it would lock an operator out of a credential
///         nobody can re-enter.
///     </para>
/// </remarks>
/// <param name="Mode">
///     The authentication mode the fields belong to, as the name it persists under: qualified by the declaring
///     family's key, or the unqualified spelling on a row written before that family qualified its names.
/// </param>
/// <param name="Fields">The named credential fields; empty when there is no credential.</param>
/// <param name="Version">Envelope version, for reading rows written by an earlier shape.</param>
public sealed record ProviderSecretEnvelope(
    string Mode,
    IReadOnlyDictionary<string, string> Fields,
    int Version = ProviderSecretEnvelope.CurrentVersion)
{
    /// <summary>The version written by this build.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    ///     When the credential stops being usable, or <see langword="null" /> for one that does not expire.
    /// </summary>
    /// <remarks>
    ///     Part of the envelope rather than a column of its own, because it is a property of the credential and
    ///     the schema does not learn what a credential is made of. A row written before this existed carries no
    ///     expiry and reads back as <see langword="null" />, which is also what an operator-entered key carries:
    ///     nothing states when such a key stops working, and nothing renews it.
    /// </remarks>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Field name for the single-string credential an API-key mode uses.</summary>
    public const string ApiKeyField = ProviderCredentialField.ApiKeyFieldName;

    /// <summary>
    ///     The values of the family's secret-marked declared fields, by declared field name; empty for a family
    ///     that declares none.
    /// </summary>
    /// <remarks>
    ///     Held separately from <see cref="Fields" /> because the two are different namespaces: a credential field's
    ///     name comes from the authentication mode, a declared field's from the family's configuration, and one
    ///     family naming both <c>apiKey</c> would otherwise store one over the other. The separation also leaves
    ///     <see cref="SingleValue" /> meaning what it did, so a family that declares a secret does not change how
    ///     its credential is read back.
    /// </remarks>
    public IReadOnlyDictionary<string, string> DeclaredSecrets { get; init; } = new Dictionary<string, string>();

    /// <summary>
    ///     The identity key of the family that wrote this envelope, or <see langword="null" /> for one written
    ///     before the key was recorded.
    /// </summary>
    /// <remarks>
    ///     Recorded so a value one family stored is not handed to another. It governs the whole envelope and not
    ///     only the declared values: field names on both axes are chosen by the family, two families are free to
    ///     choose the same one, and the credential axis already shows what happens without a discriminator, since
    ///     one authentication-mode name keys several incompatible payload shapes today.
    /// </remarks>
    public string? IdentityKey { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    ///     The single credential value, for the modes whose credential is one string. <see langword="null" /> when
    ///     the envelope carries no fields, or more than one — a caller that wants those must read
    ///     <see cref="Fields" /> by name.
    /// </summary>
    public string? SingleValue =>
        this.Fields.Count == 1 ? this.Fields.Values.First() : null;

    /// <summary>
    ///     Whether <paramref name="readingKey" /> may read this envelope: the family that wrote it, or any family
    ///     when the envelope records no key.
    /// </summary>
    /// <param name="readingKey">The identity key of the family doing the read.</param>
    public bool WrittenBy(string? readingKey)
    {
        return this.IdentityKey is null || ProviderVocabulary.KeysEqual(this.IdentityKey, readingKey);
    }

    /// <summary>
    ///     Why <paramref name="readingKey" /> may not read this envelope, naming the family that wrote it and the
    ///     one reading, or <see langword="null" /> when the read is permitted.
    /// </summary>
    /// <param name="readingKey">The identity key of the family doing the read.</param>
    /// <remarks>
    ///     The mismatch is returned for the caller to act on, and not thrown. A stored credential is read on
    ///     list paths that project many connections at once and on the redaction path that has to see every
    ///     value it might otherwise leak, and a throw on either would cost more than the mismatch it reports.
    /// </remarks>
    public string? DescribeForeignRead(string? readingKey)
    {
        return this.WrittenBy(readingKey)
            ? null
            : $"The stored credential was written by the provider family '{this.IdentityKey}' and is being read "
              + $"as '{readingKey ?? "no family"}'. A credential field means whatever the family that wrote it "
              + "says it means, so it is not handed to another family.";
    }

    /// <summary>
    ///     Reads one named field, or <see langword="null" /> when it is absent or blank. Blank counts as absent
    ///     because a field an operator left empty is stored by nothing and read by nothing.
    /// </summary>
    /// <param name="name">The field name, as the driver declared it.</param>
    public string? Field(string name)
    {
        return this.Fields.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    /// <summary>Builds an envelope for a mode whose credential is one string.</summary>
    /// <param name="mode">The authentication mode, as the name it persists under.</param>
    /// <param name="apiKey">The key; a null or blank value yields an envelope with no fields.</param>
    public static ProviderSecretEnvelope ForApiKey(string mode, string? apiKey)
    {
        return new ProviderSecretEnvelope(
            mode,
            string.IsNullOrWhiteSpace(apiKey)
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { [ApiKeyField] = apiKey });
    }

    /// <summary>Serializes the envelope for storage. The result is credential material and is protected by the caller.</summary>
    public string Encode()
    {
        return JsonSerializer.Serialize(
            new StoredShape(
                this.Version,
                this.Mode,
                this.Fields,
                this.ExpiresAt,
                this.DeclaredSecrets.Count == 0 ? null : this.DeclaredSecrets,
                this.IdentityKey),
            SerializerOptions);
    }

    /// <summary>
    ///     Reads a stored credential. A value that is not an envelope is taken as a bare single-field credential
    ///     for <paramref name="mode" />, which is how every row written before the envelope existed reads, so no
    ///     data migration is needed to adopt it.
    /// </summary>
    /// <param name="stored">The unprotected stored value; may be <see langword="null" /> or blank.</param>
    /// <param name="mode">The authentication mode recorded alongside the credential.</param>
    public static ProviderSecretEnvelope Decode(string? stored, string mode)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return new ProviderSecretEnvelope(mode, new Dictionary<string, string>());
        }

        var trimmed = stored.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            return ForApiKey(mode, stored);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<StoredShape>(stored, SerializerOptions);
            if (parsed?.Fields is null)
            {
                return ForApiKey(mode, stored);
            }

            // The stored mode wins over the caller's: the row is the record of what the credential was created
            // for, and a profile whose mode was edited without re-entering the credential must not be read as
            // though the old material fits the new mode. The writing family is recorded alongside it, so a
            // reader whose mode name means something else can detect that.
            var storedMode = string.IsNullOrWhiteSpace(parsed.Mode) ? mode : parsed.Mode;
            return new ProviderSecretEnvelope(storedMode, WithoutNulls(parsed.Fields), parsed.Version)
            {
                ExpiresAt = parsed.ExpiresAt,
                DeclaredSecrets = WithoutNulls(parsed.DeclaredSecrets),

                // An envelope written while the key was recorded against the declared values alone carries it
                // under the older name, and means the same thing: the family that wrote the envelope.
                IdentityKey = parsed.IdentityKey ?? parsed.DeclaredBy,
            };
        }
        catch (JsonException)
        {
            // A credential that merely happens to start with a brace is still a credential.
            return ForApiKey(mode, stored);
        }
    }

    // A field whose stored value is JSON null deserializes to a null string, and the dictionary this returns
    // says its values are not null. An envelope written by an older build, or edited by hand during an upgrade,
    // is the way one arrives. Dropped rather than kept, because a field with no value is a field the credential
    // does not carry.
    private static Dictionary<string, string> WithoutNulls(IReadOnlyDictionary<string, string>? fields)
    {
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in fields ?? new Dictionary<string, string>())
        {
            if (value is not null)
            {
                kept[name] = value;
            }
        }

        return kept;
    }

    /// <summary>Renders the envelope as its field names only; see <see cref="SecretSafeRendering" />.</summary>
    public override string ToString()
    {
        return $"{nameof(ProviderSecretEnvelope)} {{ Version = {this.Version}, Mode = {this.Mode}, "
               + $"Fields = [{string.Join(", ", this.Fields.Keys)}], "
               + $"IdentityKey = {this.IdentityKey ?? "none"}, "
               + $"DeclaredSecrets = [{string.Join(", ", this.DeclaredSecrets.Keys)}], "
               + $"ExpiresAt = {this.ExpiresAt?.ToString("O") ?? "none"} }}";
    }

    private sealed record StoredShape(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("fields")] IReadOnlyDictionary<string, string> Fields,
        [property: JsonPropertyName("exp")] DateTimeOffset? ExpiresAt = null,
        [property: JsonPropertyName("declared")]
        IReadOnlyDictionary<string, string>? DeclaredSecrets = null,
        [property: JsonPropertyName("key")] string? IdentityKey = null,
        [property: JsonPropertyName("declaredBy")]
        string? DeclaredBy = null);
}
