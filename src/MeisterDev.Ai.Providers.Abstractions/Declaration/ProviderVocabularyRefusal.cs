// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Declaration;

/// <summary>
///     Why an identity key or a qualified vocabulary value was refused: the rule it broke and a message naming
///     it.
/// </summary>
/// <remarks>
///     The rule is carried apart from the message so a caller can react to it without reading English. The
///     message names the one rule that failed and does not restate the rest of the grammar, because an author
///     handed the whole grammar still has to work out which part applied.
/// </remarks>
/// <param name="Rule">The rule that failed.</param>
/// <param name="Message">What was wrong, naming the rule and the value.</param>
public sealed record ProviderVocabularyRefusal(ProviderVocabularyRule Rule, string Message);
