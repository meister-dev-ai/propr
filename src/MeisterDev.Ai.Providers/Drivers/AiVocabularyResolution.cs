// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     The outcome of reading a stored or transported credential or protocol mode against the loaded families,
///     through <see cref="AiProviderLegacyNames.ResolveAuthMode" /> and
///     <see cref="AiProviderLegacyNames.ResolveProtocolMode" />.
/// </summary>
/// <remarks>
///     A value no loaded family claims, and that is not one of the host-reserved protocol modes, produces an
///     unresolved outcome. The resolution neither throws nor returns a null for one, so the caller decides what
///     happens to it: report it as it stands, refuse the read, or substitute a shape. The value is carried either
///     way, because a caller that refuses has to name what it refused, and a caller that keeps the value for
///     later needs what it read. The default instance is unresolved with an empty value, which a caller
///     holding an uninitialized outcome should act on.
/// </remarks>
public readonly record struct AiVocabularyResolution
{
    private readonly string? _name;
    private readonly string? _value;

    private AiVocabularyResolution(string? name, bool isResolved, string? value)
    {
        // Trimmed here rather than at each factory, so the value Name reports is trimmed however the outcome was
        // built.
        this._name = name?.Trim();
        this.IsResolved = isResolved;
        this._value = value;
    }

    /// <summary>The value as it was read, trimmed of surrounding whitespace.</summary>
    /// <remarks>
    ///     A resolved value keeps the spelling it was read under rather than the one the family declares now, so
    ///     a caller writing a value back can leave a row holding a superseded spelling as it is until that row's
    ///     own rewrite. <see cref="TryGetValue" /> is what answers with the declared spelling.
    /// </remarks>
    public string Name => this._name ?? string.Empty;

    /// <summary>Whether a loaded family claims this value, or the host reserves it.</summary>
    public bool IsResolved { get; }

    /// <summary>Gets the shape this value names, in the spelling it persists under.</summary>
    /// <param name="value">
    ///     The declaring family's qualified name, or the host-reserved name for a reserved protocol mode, when the
    ///     value resolved; otherwise empty.
    /// </param>
    /// <returns><see langword="true" /> when the value resolved.</returns>
    public bool TryGetValue(out string value)
    {
        value = this._value ?? string.Empty;
        return this.IsResolved;
    }

    /// <summary>
    ///     The shape this value names in the spelling it persists under, or <paramref name="fallback" /> when
    ///     nothing claims it.
    /// </summary>
    /// <param name="fallback">The value to stand in for one nothing claims.</param>
    public string ValueOr(string fallback)
    {
        return this.IsResolved ? this._value ?? string.Empty : fallback;
    }

    /// <summary>Reports a value claimed by a loaded family, or reserved by the host.</summary>
    /// <param name="name">The value as it was read.</param>
    /// <param name="value">The spelling the shape persists under.</param>
    internal static AiVocabularyResolution Resolved(string name, string value)
    {
        return new AiVocabularyResolution(name, true, value);
    }

    /// <summary>Reports a value nothing claims.</summary>
    /// <param name="name">The value as it was read.</param>
    internal static AiVocabularyResolution Unresolved(string name)
    {
        return new AiVocabularyResolution(name, false, null);
    }
}

/// <summary>
///     The outcome of resolving one stored or transported value against a host-owned enumeration, through
///     <see cref="AiVocabulary" />.
/// </summary>
/// <typeparam name="TEnum">The enumeration the value is read against.</typeparam>
/// <remarks>
///     A value that names no member of <typeparamref name="TEnum" /> produces an unresolved outcome. The helper
///     neither throws nor returns a null for one, so the caller decides what happens to it: substitute a member,
///     report the value, or refuse the read. The value read is carried either way, because a caller that reports
///     it has to name what it could not resolve. The default instance is unresolved with an empty value, which
///     is what a caller holding an uninitialized outcome should act on.
/// </remarks>
public readonly record struct AiVocabularyResolution<TEnum>
    where TEnum : struct, Enum
{
    private readonly string? _name;
    private readonly TEnum _value;

    private AiVocabularyResolution(string? name, bool isResolved, TEnum value)
    {
        // Trimmed here rather than at each factory, so the value Name reports is trimmed however the outcome was
        // built.
        this._name = name?.Trim();
        this.IsResolved = isResolved;
        this._value = value;
    }

    /// <summary>The value as it was read, trimmed of surrounding whitespace.</summary>
    public string Name => this._name ?? string.Empty;

    /// <summary>Whether the value names a member of <typeparamref name="TEnum" />.</summary>
    public bool IsResolved { get; }

    /// <summary>Gets the member this value names.</summary>
    /// <param name="value">The member, when the value resolved; otherwise the enum's default member.</param>
    /// <returns><see langword="true" /> when the value resolved.</returns>
    public bool TryGetValue(out TEnum value)
    {
        value = this._value;
        return this.IsResolved;
    }

    /// <summary>Reports a value that names <paramref name="value" />.</summary>
    /// <param name="name">The value as it was read.</param>
    /// <param name="value">The member it names.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="value" /> is not a declared member of <typeparamref name="TEnum" />.
    /// </exception>
    internal static AiVocabularyResolution<TEnum> Resolved(string name, TEnum value)
    {
        // A resolved outcome states that the value names a member this build declares, and every reader of
        // TryGetValue acts on it. Carrying an undeclared member would make that statement false at the one place
        // it is established.
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"'{typeof(TEnum).Name}' declares no such member, so the value did not resolve.");
        }

        return new AiVocabularyResolution<TEnum>(name, true, value);
    }

    /// <summary>Reports a value that names no member this build knows.</summary>
    /// <param name="name">The value as it was read.</param>
    internal static AiVocabularyResolution<TEnum> Unresolved(string name)
    {
        return new AiVocabularyResolution<TEnum>(name, false, default);
    }
}
