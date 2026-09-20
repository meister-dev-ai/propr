// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Declaration;

/// <summary>
///     How long an action of this family may stay open before the host expires it.
/// </summary>
/// <remarks>
///     <para>
///         An action's work outlives the dispatch that started it: an operator signing in at a vendor takes
///         minutes, and the completion arrives on a path the request that opened it no longer holds. The host
///         therefore opens an invocation with a bounded window and expires it rather than leaving it pending, and
///         the family is the party that knows how long its own flow can legitimately take.
///     </para>
///     <para>
///         Where a declared field carries the value the vendor states — a credential lifetime, a handshake
///         expiry — naming that field derives the window from it and <see cref="Maximum" /> is the ceiling the
///         host applies when the field is absent or larger.
///     </para>
/// </remarks>
/// <param name="Maximum">The longest an invocation of this family may stay open.</param>
/// <param name="DerivedFromFieldName">
///     A declared field holding the window in seconds, or null when <paramref name="Maximum" /> is the whole
///     rule.
/// </param>
/// <exception cref="ArgumentOutOfRangeException"><paramref name="Maximum" /> is zero or negative.</exception>
/// <exception cref="ArgumentException"><paramref name="DerivedFromFieldName" /> is present and blank.</exception>
public sealed record ProviderInvocationWindow(TimeSpan Maximum, string? DerivedFromFieldName = null)
{
    // Held in fields so that setting either goes through an accessor that checks it, and both are declared here
    // in the order of the parameters so the positional order is unchanged.
    private readonly TimeSpan _maximum = PositiveWindow(Maximum, nameof(Maximum));
    private readonly string? _derivedFromFieldName = FieldNameOrNone(DerivedFromFieldName, nameof(DerivedFromFieldName));

    /// <summary>
    ///     The longest an invocation of this family may stay open. Zero or negative is refused: the host expires
    ///     an invocation against this, so such a window would expire every invocation before its flow could
    ///     start and the family would look broken rather than misdeclared.
    /// </summary>
    public TimeSpan Maximum
    {
        get => this._maximum;
        init => this._maximum = PositiveWindow(value, nameof(ProviderInvocationWindow.Maximum));
    }

    /// <summary>
    ///     A declared field holding the window in seconds, or null when <see cref="Maximum" /> is the whole rule.
    ///     A blank name is refused rather than read as absent, because it names no field and the host would look
    ///     one up under an empty key.
    /// </summary>
    public string? DerivedFromFieldName
    {
        get => this._derivedFromFieldName;
        init => this._derivedFromFieldName =
            FieldNameOrNone(value, nameof(ProviderInvocationWindow.DerivedFromFieldName));
    }

    private static TimeSpan PositiveWindow(TimeSpan maximum, string parameter)
    {
        return maximum > TimeSpan.Zero
            ? maximum
            : throw new ArgumentOutOfRangeException(
                parameter,
                maximum,
                "An invocation window is the time an action may stay open, so it is longer than nothing.");
    }

    private static string? FieldNameOrNone(string? fieldName, string parameter)
    {
        if (fieldName is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(fieldName))
        {
            throw new ArgumentException(
                "A derived-from field name names a declared field. Leave it unset for a window that is the "
                + "maximum alone.",
                parameter);
        }

        // Refused rather than trimmed, because the host looks the name up against the declared fields by exact
        // ordinal comparison. A padded name matches nothing there and the window silently falls back to the
        // maximum, which is the declared bound the family was narrowing.
        return string.Equals(fieldName, fieldName.Trim(), StringComparison.Ordinal)
            ? fieldName
            : throw new ArgumentException(
                $"The derived-from field name '{fieldName}' is padded. The host reads the connection's value by "
                + "this name exactly, so a padded one names no declared field.",
                parameter);
    }
}
