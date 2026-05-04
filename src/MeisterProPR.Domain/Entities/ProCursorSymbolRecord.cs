// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Deterministic definition-level symbol record discovered during ProCursor indexing.
/// </summary>
public sealed class ProCursorSymbolRecord
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ProCursorSymbolRecord" /> class.
    /// </summary>
    public ProCursorSymbolRecord(
        Guid id,
        Guid snapshotId,
        string language,
        string symbolKey,
        string displayName,
        string symbolKind,
        string? containingSymbolKey,
        string filePath,
        int lineStart,
        int lineEnd,
        string signature,
        string searchText)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (snapshotId == Guid.Empty)
        {
            throw new ArgumentException("SnapshotId must not be empty.", nameof(snapshotId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(lineStart, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(lineEnd, lineStart);

        this.Id = id;
        this.SnapshotId = snapshotId;
        this.Language = NormalizeRequired(language, nameof(language));
        this.SymbolKey = NormalizeRequired(symbolKey, nameof(symbolKey));
        this.DisplayName = NormalizeRequired(displayName, nameof(displayName));
        this.SymbolKind = NormalizeRequired(symbolKind, nameof(symbolKind));
        this.ContainingSymbolKey = NormalizeOptional(containingSymbolKey);
        this.FilePath = NormalizeRequired(filePath, nameof(filePath));
        this.LineStart = lineStart;
        this.LineEnd = lineEnd;
        this.Signature = NormalizeRequired(signature, nameof(signature));
        this.SearchText = NormalizeRequired(searchText, nameof(searchText));
    }

    private ProCursorSymbolRecord()
    {
    }

    /// <summary>
    ///     Gets the unique identifier for this symbol record.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    ///     Gets the snapshot identifier this symbol record belongs to.
    /// </summary>
    public Guid SnapshotId { get; private set; }

    /// <summary>
    ///     Gets the programming language of the symbol.
    /// </summary>
    public string Language { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the unique key identifying this symbol.
    /// </summary>
    public string SymbolKey { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the display name of the symbol.
    /// </summary>
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the kind of symbol (e.g., class, method, property).
    /// </summary>
    public string SymbolKind { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the key of the containing symbol, if any.
    /// </summary>
    public string? ContainingSymbolKey { get; private set; }

    /// <summary>
    ///     Gets the file path where the symbol is located.
    /// </summary>
    public string FilePath { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the starting line number of the symbol definition.
    /// </summary>
    public int LineStart { get; private set; }

    /// <summary>
    ///     Gets the ending line number of the symbol definition.
    /// </summary>
    public int LineEnd { get; private set; }

    /// <summary>
    ///     Gets the signature of the symbol.
    /// </summary>
    public string Signature { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the search text for the symbol.
    /// </summary>
    public string SearchText { get; private set; } = string.Empty;

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }
}
