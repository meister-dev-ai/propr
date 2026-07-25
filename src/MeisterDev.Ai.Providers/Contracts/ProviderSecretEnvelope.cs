// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     The internal shape of a stored provider credential: an authentication mode and the named fields that mode
///     needs. One credential is one opaque blob to whatever stores it, and this is the only thing that knows what
///     is inside.
/// </summary>
/// <remarks>
///     <para>
///         An API key is one string, but the credentials that arrive with native providers are not: SigV4 needs an
///         access key id, a secret access key and sometimes a session token; a Google service account is a JSON
///         document. Without a defined shape, each provider would invent its own encoding inside the same opaque
///         column, and the first one to need two fields would either add a column or pack them with a separator
///         nobody else knows about.
///     </para>
///     <para>
///         The envelope is versioned so a shape can change without guessing at what an old row means, and it
///         tolerates a bare string, which is what rows written before it existed contain.
///     </para>
/// </remarks>
/// <param name="Mode">The authentication mode the fields belong to.</param>
/// <param name="Fields">The named credential fields; empty when there is no credential.</param>
/// <param name="Version">Envelope version, for reading rows written by an earlier shape.</param>
public sealed record ProviderSecretEnvelope(
    AiAuthMode Mode,
    IReadOnlyDictionary<string, string> Fields,
    int Version = ProviderSecretEnvelope.CurrentVersion)
{
    /// <summary>The version written by this build.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Field name for the single-string credential an API-key mode uses.</summary>
    public const string ApiKeyField = "apiKey";

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

    /// <summary>Builds an envelope for a mode whose credential is one string.</summary>
    /// <param name="mode">The authentication mode.</param>
    /// <param name="apiKey">The key; a null or blank value yields an envelope with no fields.</param>
    public static ProviderSecretEnvelope ForApiKey(AiAuthMode mode, string? apiKey)
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
            new StoredShape(this.Version, this.Mode.ToString(), this.Fields),
            SerializerOptions);
    }

    /// <summary>
    ///     Reads a stored credential. A value that is not an envelope is taken as a bare single-field credential
    ///     for <paramref name="mode" />, which is how every row written before the envelope existed reads, so no
    ///     data migration is needed to adopt it.
    /// </summary>
    /// <param name="stored">The unprotected stored value; may be <see langword="null" /> or blank.</param>
    /// <param name="mode">The authentication mode recorded alongside the credential.</param>
    public static ProviderSecretEnvelope Decode(string? stored, AiAuthMode mode)
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
            // though the old material fits the new mode.
            var storedMode = Enum.TryParse<AiAuthMode>(parsed.Mode, out var parsedMode) ? parsedMode : mode;
            return new ProviderSecretEnvelope(storedMode, parsed.Fields, parsed.Version);
        }
        catch (JsonException)
        {
            // A credential that merely happens to start with a brace is still a credential.
            return ForApiKey(mode, stored);
        }
    }

    /// <summary>Renders the envelope as its field names only; see <see cref="SecretSafeRendering" />.</summary>
    public override string ToString()
    {
        return $"{nameof(ProviderSecretEnvelope)} {{ Version = {this.Version}, Mode = {this.Mode}, "
               + $"Fields = [{string.Join(", ", this.Fields.Keys)}] }}";
    }

    private sealed record StoredShape(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("fields")] IReadOnlyDictionary<string, string> Fields);
}
