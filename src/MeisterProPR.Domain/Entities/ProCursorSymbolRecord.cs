// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Deterministic definition-level symbol record discovered during ProCursor indexing.
/// </summary>
public sealed class ProCursorSymbolRecord
{
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

        if (lineStart < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lineStart));
        }

        if (lineEnd < lineStart)
        {
            throw new ArgumentOutOfRangeException(nameof(lineEnd));
        }

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

    public Guid Id { get; private set; }

    public Guid SnapshotId { get; private set; }

    public string Language { get; private set; } = string.Empty;

    public string SymbolKey { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public string SymbolKind { get; private set; } = string.Empty;

    public string? ContainingSymbolKey { get; private set; }

    public string FilePath { get; private set; } = string.Empty;

    public int LineStart { get; private set; }

    public int LineEnd { get; private set; }

    public string Signature { get; private set; } = string.Empty;

    public string SearchText { get; private set; } = string.Empty;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeRequired(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }
}
