// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     Which quantity a projected daily count measures.
/// </summary>
/// <remarks>
///     Custom type tags are deliberately absent. Cross-client aggregates must never include them (two clients'
///     identically-named custom tags are not the same thing), so their only consumer is a single client's own
///     view: a bounded, one-client read straight from the assignment table. Projecting them would mean a
///     polymorphic dimension key holding a tag identity, for no reading benefit.
/// </remarks>
public enum CodeInsightCountDimension
{
    // Persisted by ordinal: keep these values explicit and do NOT reorder or renumber, or historical
    // projection rows would silently start counting a different quantity.

    /// <summary>Findings produced. The dimension key is the empty string.</summary>
    FindingTotal = 0,

    /// <summary>
    ///     Findings carrying one core type. The dimension key is the type's stable slug, which is comparable
    ///     across clients. A finding with several types contributes to several rows, by design: the counts
    ///     answer "how many findings touch this type", not "how many findings are only this type".
    /// </summary>
    CoreType = 1,

    /// <summary>
    ///     Findings whose review thread resolved to one outcome. The dimension key is the disposition's name.
    /// </summary>
    Disposition = 2,

    /// <summary>
    ///     Findings placed inside a definition. The dimension key is that definition's name, and the cell's own
    ///     file path is what disambiguates it: the name is name-based, so two files may hold one spelling.
    /// </summary>
    /// <remarks>
    ///     Findings the file's syntax could not place produce no cell at all, so a symbol-grained reading covers
    ///     less than a file-grained one. That difference is a fact about the data and has to be surfaced, never
    ///     closed by inventing a bucket for it.
    /// </remarks>
    Symbol = 3,
}
