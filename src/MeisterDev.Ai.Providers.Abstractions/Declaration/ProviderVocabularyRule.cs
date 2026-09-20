// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Declaration;

/// <summary>The token rule a declared identity key or vocabulary value broke.</summary>
public enum ProviderVocabularyRule
{
    /// <summary>Nothing was supplied where a value is required.</summary>
    Missing = 0,

    /// <summary>The value is longer than the column that stores it.</summary>
    Length = 1,

    /// <summary>The separators are missing, repeated, or in a position that leaves one part empty.</summary>
    Separator = 2,

    /// <summary>The value carries a character outside the allowed set.</summary>
    Characters = 3,
}
