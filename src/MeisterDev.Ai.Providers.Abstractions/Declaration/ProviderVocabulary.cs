// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Declaration;

/// <summary>
///     The string format a provider identity key and a qualified vocabulary value are written in: its bounds,
///     its separators, how two of its values compare, how one is checked, and how one is composed and split.
/// </summary>
/// <remarks>
///     <para>
///         An identity key persists against every connection of a family, and a declared authentication or
///         protocol mode persists as that key joined to the mode name. Both are bounded because the columns that
///         hold them are bounded, and both are restricted to one character set because the join has to be
///         reversible: the separator is a character the key cannot contain, so a stored value splits into exactly
///         one key and one mode name.
///     </para>
///     <para>
///         The bounds, the comparison, the checks and the two operations sit in one type because they are one
///         format. A caller that composes a value, stores it, reads it back and compares it uses all four, and
///         two copies of the rules would let a value be accepted at declaration and refused at storage.
///     </para>
/// </remarks>
public static class ProviderVocabulary
{
    /// <summary>
    ///     The longest an identity key may be, matching the narrowest column that stores one:
    ///     <c>client_token_usage_samples.provider_kind</c>, which sits inside a unique index and is not widened.
    /// </summary>
    public const int MaximumKeyLength = 64;

    /// <summary>The longest a mode name may be. The same bound as a key, without room for the key separator.</summary>
    public const int MaximumModeNameLength = 64;

    /// <summary>
    ///     The longest a qualified value may be: a key, the qualifier separator, and a mode name.
    /// </summary>
    public const int MaximumQualifiedValueLength = MaximumKeyLength + 1 + MaximumModeNameLength;

    /// <summary>The single separator between a key's vendor prefix and its internal name.</summary>
    public const char KeySeparator = '/';

    /// <summary>The single separator between an identity key and the mode name it qualifies.</summary>
    public const char QualifierSeparator = ':';

    /// <summary>
    ///     How two keys are compared. Case is folded, so two spellings differing only in case name one family and
    ///     not two. The declared spelling is the one that persists.
    /// </summary>
    public static StringComparer KeyComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Whether two keys name the same family.</summary>
    /// <param name="left">One key, or null.</param>
    /// <param name="right">The other key, or null.</param>
    public static bool KeysEqual(string? left, string? right)
    {
        return KeyComparer.Equals(left, right);
    }

    /// <summary>
    ///     How two vocabulary values are compared: over the whole value, folding case.
    /// </summary>
    /// <remarks>
    ///     The whole value, because two families may declare the same mode name and only the qualifier tells them
    ///     apart. Comparing the mode name alone would treat one family's protocol mode as another's. Case is
    ///     folded, as it is for a key, so a value read back in another casing names the mode it was written as.
    /// </remarks>
    public static StringComparer ValueComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Whether two vocabulary values name the same mode.</summary>
    /// <param name="left">One value, or null.</param>
    /// <param name="right">The other value, or null.</param>
    public static bool ValuesEqual(string? left, string? right)
    {
        return ValueComparer.Equals(left, right);
    }

    /// <summary>Whether <paramref name="values" /> contains <paramref name="value" />.</summary>
    /// <param name="values">The values to search.</param>
    /// <param name="value">The value to look for, or null.</param>
    public static bool Names(IEnumerable<string> values, string? value)
    {
        ArgumentNullException.ThrowIfNull(values);

        return value is not null && values.Contains(value, ValueComparer);
    }

    /// <summary>Whether <paramref name="key" /> is a well-formed identity key.</summary>
    /// <param name="key">The key to check.</param>
    public static bool IsValidIdentityKey(string? key)
    {
        return ValidateKey(key) is null;
    }

    /// <summary>
    ///     Checks an identity key, naming the rule it broke.
    /// </summary>
    /// <param name="key">The key as declared.</param>
    /// <returns>The refusal, or <see langword="null" /> when the key is well formed.</returns>
    public static ProviderVocabularyRefusal? ValidateKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return new ProviderVocabularyRefusal(
                ProviderVocabularyRule.Missing,
                "An identity key is a vendor prefix, one '/', and an internal name. Nothing was supplied.");
        }

        if (key.Length > MaximumKeyLength)
        {
            return new ProviderVocabularyRefusal(
                ProviderVocabularyRule.Length,
                $"The identity key '{key}' is {key.Length} characters. An identity key is at most "
                + $"{MaximumKeyLength} characters including the separator, which is the width of the narrowest "
                + "column that stores one.");
        }

        var separator = key.IndexOf(KeySeparator, StringComparison.Ordinal);
        if (separator < 0)
        {
            return new ProviderVocabularyRefusal(
                ProviderVocabularyRule.Separator,
                $"The identity key '{key}' has no '{KeySeparator}'. A key is a vendor prefix and an internal "
                + $"name joined by exactly one '{KeySeparator}'.");
        }

        if (separator == 0 || separator == key.Length - 1 || key.IndexOf(KeySeparator, separator + 1) >= 0)
        {
            return new ProviderVocabularyRefusal(
                ProviderVocabularyRule.Separator,
                $"The identity key '{key}' does not have exactly one '{KeySeparator}' with a vendor prefix "
                + "before it and an internal name after it.");
        }

        return DescribeDisallowedCharacter(key, allowKeySeparator: true)
               ?? DescribeQualifierSeparator(key, "identity key");
    }

    /// <summary>
    ///     Checks a mode name, naming the rule it broke.
    /// </summary>
    /// <param name="modeName">The mode name as declared.</param>
    /// <returns>The refusal, or <see langword="null" /> when the mode name is well formed.</returns>
    public static ProviderVocabularyRefusal? ValidateModeName(string? modeName)
    {
        if (string.IsNullOrEmpty(modeName))
        {
            return new ProviderVocabularyRefusal(
                ProviderVocabularyRule.Missing,
                "A mode name is ASCII letters, digits and hyphen. Nothing was supplied.");
        }

        if (modeName.Length > MaximumModeNameLength)
        {
            return new ProviderVocabularyRefusal(
                ProviderVocabularyRule.Length,
                $"The mode name '{modeName}' is {modeName.Length} characters. A mode name is at most "
                + $"{MaximumModeNameLength} characters.");
        }

        if (modeName.IndexOf(KeySeparator, StringComparison.Ordinal) >= 0)
        {
            return new ProviderVocabularyRefusal(
                ProviderVocabularyRule.Separator,
                $"The mode name '{modeName}' contains '{KeySeparator}', which belongs to the identity key that "
                + "qualifies it.");
        }

        return DescribeDisallowedCharacter(modeName, allowKeySeparator: false)
               ?? DescribeQualifierSeparator(modeName, "mode name");
    }

    /// <summary>
    ///     Joins an identity key and a mode name into the value that persists.
    /// </summary>
    /// <param name="key">The key of the family that declared the mode.</param>
    /// <param name="modeName">The mode name as the family declared it.</param>
    /// <returns>The qualified value.</returns>
    /// <exception cref="ArgumentException">Either part breaks a token rule; the message names which.</exception>
    public static string Compose(string key, string modeName)
    {
        if (ValidateKey(key) is { } keyRefusal)
        {
            throw new ArgumentException(keyRefusal.Message, nameof(key));
        }

        if (ValidateModeName(modeName) is { } modeRefusal)
        {
            throw new ArgumentException(modeRefusal.Message, nameof(modeName));
        }

        return string.Concat(key, QualifierSeparator.ToString(), modeName);
    }

    /// <summary>
    ///     Splits a stored value into the key that qualifies it and the mode name it carries.
    /// </summary>
    /// <param name="value">The value as it was read; whitespace around it is ignored.</param>
    /// <returns>
    ///     The key and the mode name. A value carrying no qualifier splits into no key and the whole value as the
    ///     mode name. The host-reserved protocol values hold that form, as does every row written before a family
    ///     declared its key.
    /// </returns>
    public static ProviderQualifiedValue Split(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        var separator = trimmed.IndexOf(QualifierSeparator, StringComparison.Ordinal);

        return separator < 0
            ? new ProviderQualifiedValue(null, trimmed)
            : new ProviderQualifiedValue(trimmed[..separator], trimmed[(separator + 1)..]);
    }

    /// <summary>Whether <paramref name="value" /> is a well-formed qualified value.</summary>
    /// <param name="value">The value to check.</param>
    public static bool IsWellFormedQualifiedValue(string? value)
    {
        return ValidateQualifiedValue(value) is null;
    }

    /// <summary>
    ///     Checks a qualified value, naming the rule it broke.
    /// </summary>
    /// <param name="value">The value as it would persist.</param>
    /// <returns>The refusal, or <see langword="null" /> when the value is well formed.</returns>
    public static ProviderVocabularyRefusal? ValidateQualifiedValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return new ProviderVocabularyRefusal(
                ProviderVocabularyRule.Missing,
                "A qualified value is an identity key, one ':', and a mode name. Nothing was supplied.");
        }

        if (value.Length > MaximumQualifiedValueLength)
        {
            return new ProviderVocabularyRefusal(
                ProviderVocabularyRule.Length,
                $"The qualified value '{value}' is {value.Length} characters. A qualified value is at most "
                + $"{MaximumQualifiedValueLength} characters.");
        }

        var split = Split(value);
        if (split.Key is null)
        {
            return new ProviderVocabularyRefusal(
                ProviderVocabularyRule.Separator,
                $"The value '{value}' has no '{QualifierSeparator}'. A qualified value carries the key of the "
                + "family that declared the mode.");
        }

        return ValidateKey(split.Key) ?? ValidateModeName(split.ModeName);
    }

    private static ProviderVocabularyRefusal? DescribeDisallowedCharacter(string token, bool allowKeySeparator)
    {
        foreach (var character in token)
        {
            var allowed = (allowKeySeparator && character == KeySeparator)
                          || character == '-'
                          || character is >= '0' and <= '9'
                          || character is >= 'a' and <= 'z'
                          || character is >= 'A' and <= 'Z';
            if (allowed)
            {
                continue;
            }

            return new ProviderVocabularyRefusal(
                ProviderVocabularyRule.Characters,
                $"'{token}' contains '{character}'. ASCII letters, digits and hyphen are allowed"
                + (allowKeySeparator ? $", plus one '{KeySeparator}'." : "."));
        }

        return null;
    }

    // Reported apart from the general character rule. This character breaks the split and not only the character
    // set: a key or a mode name carrying it would produce a value that splits into a different pair than it was
    // composed from.
    private static ProviderVocabularyRefusal? DescribeQualifierSeparator(string token, string part)
    {
        return token.IndexOf(QualifierSeparator, StringComparison.Ordinal) < 0
            ? null
            : new ProviderVocabularyRefusal(
                ProviderVocabularyRule.Characters,
                $"The {part} '{token}' contains '{QualifierSeparator}', which joins an identity key to the mode "
                + "name it qualifies and cannot appear inside either.");
    }
}
