// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using MeisterDev.Ai.Providers.Drivers;

namespace MeisterDev.Ai.Providers.AddIns;

/// <summary>The outcome of the one discovery pass: what loaded, what did not, and the drivers that resulted.</summary>
/// <remarks>
///     <para>
///         Discovery runs once while the host starts and the directories are never rescanned, so this records
///         load time rather than what the directories hold now. An assembly deleted from a mounted volume under
///         a running host still appears here and still serves.
///     </para>
///     <para>
///         A class and not a record, because activating an add-in changes it. Value equality over lists that
///         move, and a <c>with</c> handing a copy the same lock while the two diverge, are both traps this type
///         would carry for no benefit: nothing compares two catalogs.
///     </para>
/// </remarks>
public sealed class ProviderAddInCatalog
{
    // One field, so a reader takes all four lists as they stood at one moment. Four fields would each be read
    // separately: a page reading Loaded before an activation and Awaiting after it would show the add-in in
    // neither list. Writes are rare enough that rebuilding the snapshot costs nothing worth measuring.
    private readonly Lock _gate = new();
    private Snapshot _contents;

    /// <summary>Records what one discovery pass found.</summary>
    /// <param name="loaded">The add-ins that were taken, in the order they were read.</param>
    /// <param name="rejected">The assemblies that were skipped, in the order they were read.</param>
    /// <param name="drivers">The drivers the loaded add-ins supplied, one per loaded add-in.</param>
    /// <param name="awaiting">The external add-ins nobody has activated, described from their files.</param>
    public ProviderAddInCatalog(
        IReadOnlyList<LoadedProviderAddIn> loaded,
        IReadOnlyList<RejectedProviderAddIn> rejected,
        IReadOnlyList<IAiProviderDriver> drivers,
        IReadOnlyList<DiscoveredProviderAddIn> awaiting)
    {
        // Copied on the way in. The catalog is one instance for the process, and a read-only list over the List
        // the discovery pass built is cast back to it in one line, which would let a caller change what the
        // registry and the inventory read afterwards.
        this._contents = new Snapshot(
            Held(loaded, nameof(loaded)),
            Held(rejected, nameof(rejected)),
            Held(drivers, nameof(drivers)),
            Held(awaiting, nameof(awaiting)));
    }

    /// <summary>The add-ins that were taken, in the order they were read.</summary>
    public IReadOnlyList<LoadedProviderAddIn> Loaded => Volatile.Read(ref this._contents).Loaded;

    /// <summary>The assemblies that were skipped, in the order they were read.</summary>
    public IReadOnlyList<RejectedProviderAddIn> Rejected => Volatile.Read(ref this._contents).Rejected;

    /// <summary>The drivers the loaded add-ins supplied, one per loaded add-in.</summary>
    public IReadOnlyList<IAiProviderDriver> Drivers => Volatile.Read(ref this._contents).Drivers;

    /// <summary>
    ///     The add-ins found in the external directory that nobody has activated, described from the file with
    ///     none of them executed.
    /// </summary>
    public IReadOnlyList<DiscoveredProviderAddIn> Awaiting => Volatile.Read(ref this._contents).Awaiting;

    /// <summary>
    ///     Moves one add-in from awaiting to loaded, after an administrator activated it and it loaded.
    /// </summary>
    /// <param name="contentHash">The bytes that were activated.</param>
    /// <param name="loaded">What the family turned out to be once it ran.</param>
    /// <param name="driver">The driver it supplied.</param>
    /// <remarks>
    ///     The one thing that changes this record after the discovery pass built it. Discovery is still one pass
    ///     and the directories are still never rescanned; what an activation adds is a file the pass already
    ///     found and described, now loaded. The lock serialises two activations against each other, and the one
    ///     write publishes all four lists together, so a reader sees the add-in either awaiting or loaded.
    /// </remarks>
    public void Activated(string contentHash, LoadedProviderAddIn loaded, IAiProviderDriver driver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(driver);

        lock (this._gate)
        {
            var current = this._contents;

            Volatile.Write(
                ref this._contents,
                new Snapshot(
                    [.. current.Loaded, loaded],
                    current.Rejected,
                    [.. current.Drivers, driver],
                    [
                        .. current.Awaiting.Where(found =>
                            !string.Equals(found.ContentHash, contentHash, StringComparison.Ordinal)),
                    ]));
        }
    }

    private static IReadOnlyList<T> Held<T>(IReadOnlyList<T> value, string parameter)
    {
        return value is null ? throw new ArgumentNullException(parameter) : value.ToImmutableArray();
    }

    /// <summary>The four lists as they stood at one moment, replaced whole and never edited in place.</summary>
    private sealed record Snapshot(
        IReadOnlyList<LoadedProviderAddIn> Loaded,
        IReadOnlyList<RejectedProviderAddIn> Rejected,
        IReadOnlyList<IAiProviderDriver> Drivers,
        IReadOnlyList<DiscoveredProviderAddIn> Awaiting);
}
