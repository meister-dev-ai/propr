// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Catalog;

/// <summary>
///     Reads a catalog snapshot in some source's own format and yields entries in this library's normalized
///     shape. The normalized shape is the contract; the source format is not, so a different database can be
///     adopted by adding an importer rather than by changing everything downstream of it.
/// </summary>
public interface ICatalogSnapshotImporter
{
    /// <summary>Identifies the snapshot format this importer understands, for diagnostics and provenance.</summary>
    string SourceFormat { get; }

    /// <summary>
    ///     Parses <paramref name="snapshot" /> into normalized entries. Malformed or unrecognised entries are
    ///     skipped rather than failing the whole import, because a snapshot is third-party data that may gain
    ///     fields or models this build has never seen.
    /// </summary>
    /// <param name="snapshot">The raw snapshot content.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ProviderCatalogEntry>> ImportAsync(Stream snapshot, CancellationToken ct = default);
}
