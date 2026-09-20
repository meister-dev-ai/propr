// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.AddIns;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>What an activation or a revocation did.</summary>
/// <param name="Activated">The family that is now serving, or null when nothing was.</param>
/// <param name="Refusal">Why it was refused, or null when it was not.</param>
public sealed record ProviderAddInActivationOutcome(LoadedProviderAddIn? Activated, string? Refusal);

/// <summary>
///     Activates one add-in a platform administrator decided to run, and revokes one they decided not to.
/// </summary>
/// <remarks>
///     <para>
///         An activation loads the add-in there and then, so a family an administrator turned on is serving
///         before they leave the page. The row, the in-memory gate, the catalog and the registry are all changed
///         by this one call, and the row is written last: an add-in that turns out not to load leaves nothing
///         behind saying it was approved.
///     </para>
///     <para>
///         A revocation writes only the row. An assembly cannot be unloaded from under the connections using it,
///         and a review holding a client that family built is in flight, so what revoking does is stop the next
///         start taking it. The page says that rather than implying the family stops now.
///     </para>
/// </remarks>
public sealed partial class ProviderAddInActivationService(
    ProviderAddInCatalog catalog,
    ProviderAddInActivations activations,
    AiProviderRegistry registry,
    IProviderConnectionOwnerRoles ownerRoles,
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<ProviderAddInActivationService> logger)
{
    /// <summary>Activates the add-in whose bytes hash to <paramref name="contentHash" />.</summary>
    /// <param name="contentHash">The bytes the administrator is approving.</param>
    /// <param name="adminId">The administrator making the decision.</param>
    /// <param name="ct">Cancels the write.</param>
    public async Task<ProviderAddInActivationOutcome> ActivateAsync(
        string contentHash,
        Guid? adminId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);

        var found = catalog.Awaiting.FirstOrDefault(entry =>
            string.Equals(entry.ContentHash, contentHash, StringComparison.Ordinal));

        if (found is null)
        {
            return new ProviderAddInActivationOutcome(
                null,
                "No add-in waiting for activation has those bytes. The page may be showing a pass from before "
                + "the file changed; reload it and look at what is there now.");
        }

        if (found.Refusal is { } stated)
        {
            return new ProviderAddInActivationOutcome(null, stated);
        }

        // Re-read the file, because the discovery pass ran when the host started and the bytes on disk may not
        // be the ones described here any more. Activating on the description while loading something else is
        // the one thing this gate exists to prevent.
        if (await HashOnDiskAsync(found.FilePath, ct).ConfigureAwait(false) is not { } onDisk
            || !string.Equals(onDisk, contentHash, StringComparison.Ordinal))
        {
            return new ProviderAddInActivationOutcome(
                null,
                "The file has changed since the host read it, so what is on disk is not what this page "
                + "described. Restart the host to read it again, then activate what it finds.");
        }

        // Asked before the assembly is loaded. An identity this host already serves is refused either way, by
        // the registry, and asking here means the refusal costs no load context: one is created to read a
        // driver and cannot be unloaded from under a process that has touched it.
        if (found.Key is { } stating && registry.IsRegistered(stating))
        {
            return new ProviderAddInActivationOutcome(
                null,
                $"The identity '{stating}' is already served by this host. A connection is stored against that "
                + "key, so it names one family.");
        }

        var (loaded, driver, refusal) = ProviderAddInLoader.LoadOne(
            found.FilePath,
            Path.GetDirectoryName(found.FilePath)!,
            found.Origin,
            logger);

        if (loaded is null || driver is null)
        {
            return new ProviderAddInActivationOutcome(null, refusal);
        }

        // The bytes that were loaded, not the bytes that were hashed a moment ago. The check above read the
        // file and the load read it again, and a file replaced between the two would otherwise be loaded and
        // then recorded under the hash an administrator approved.
        if (!string.Equals(loaded.ContentHash, contentHash, StringComparison.Ordinal))
        {
            return new ProviderAddInActivationOutcome(
                null,
                "The file changed while it was being read, so what was loaded is not what this page described. "
                + "Nothing was recorded. Restart the host to read the directory again.");
        }

        // What it said about itself is what the administrator approved. A driver that says something else is
        // refused rather than served, because the decision was made about the first.
        if (ProviderAddInAgreement.Disagreement(found, loaded) is { } disagreement)
        {
            return new ProviderAddInActivationOutcome(null, disagreement);
        }

        // Read here rather than taken from the caller, so what is recorded is the name the installation holds
        // for that administrator and not whatever a request said it was.
        var adminDisplayName = await ownerRoles.DescribeAdministratorAsync(adminId, ct).ConfigureAwait(false)
                               ?? "a platform administrator";

        // Written before the driver reaches the registry. A registry that took it and a write that then failed
        // would leave the family serving with nothing recording that anyone approved it, which is the state the
        // gate exists to prevent, and it would disappear at the next start with nothing saying why. The other
        // order costs a moment where the row exists and the family is not yet serving, which the next start
        // settles by loading it.
        await using (var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            db.ProviderAddInActivations.Add(
                new ProviderAddInActivationRecord
                {
                    Id = Guid.NewGuid(),
                    ContentHash = contentHash,
                    FamilyKey = loaded.Key,
                    Label = loaded.Label,
                    Version = loaded.Version,
                    FilePath = loaded.FilePath,
                    ActivatedByAdminId = adminId,
                    ActivatedByDisplayName = adminDisplayName,
                    ActivatedAt = timeProvider.GetUtcNow(),
                });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        try
        {
            registry.Include(driver);
        }
        catch (InvalidOperationException conflict)
        {
            // The row stands: an administrator approved these bytes and that decision is theirs. What failed is
            // this host's composition, which the next start reports through the same path the pass does.
            LogActivatedAddInNotRegistered(logger, loaded.Key, conflict);

            return new ProviderAddInActivationOutcome(null, conflict.Message);
        }

        activations.Include(contentHash);
        catalog.Activated(contentHash, loaded, driver);
        LogAddInActivated(logger, loaded.Key, loaded.Version, loaded.FilePath, adminDisplayName);

        return new ProviderAddInActivationOutcome(loaded, null);
    }

    /// <summary>
    ///     Loads every add-in this installation has already activated, once the schema is up to date.
    /// </summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>How many were loaded.</returns>
    /// <remarks>
    ///     <para>
    ///         The discovery pass runs during composition and reads no database, so every external add-in is
    ///         described and none is loaded. This is where the rows are read and the described ones an
    ///         administrator has approved become families the host serves.
    ///     </para>
    ///     <para>
    ///         One that does not load is recorded and passed over. The host starts either way: an add-in that
    ///         stopped loading between two starts is a reason to say so on the add-ins page, not a reason to
    ///         leave an installation without its other families.
    ///     </para>
    /// </remarks>
    public async Task<int> LoadActivatedAsync(CancellationToken ct = default)
    {
        var approved = await this.ListAsync(ct).ConfigureAwait(false);
        if (approved.Count == 0)
        {
            return 0;
        }

        var byHash = approved.ToDictionary(row => row.ContentHash, StringComparer.Ordinal);
        var loadedCount = 0;

        // Taken as a snapshot, because loading one moves it out of the list being read.
        foreach (var found in catalog.Awaiting.ToList())
        {
            if (found.ContentHash is not { } hash || !byHash.ContainsKey(hash))
            {
                continue;
            }

            var (loaded, driver, refusal) = ProviderAddInLoader.LoadOne(
                found.FilePath,
                Path.GetDirectoryName(found.FilePath)!,
                found.Origin,
                logger);

            if (loaded is null
                || driver is null
                || !string.Equals(loaded.ContentHash, hash, StringComparison.Ordinal)
                || ProviderAddInAgreement.Disagreement(found, loaded) is not null)
            {
                LogActivatedAddInNotLoaded(logger, found.FilePath, refusal ?? "it no longer matches what was approved");
                continue;
            }

            try
            {
                registry.Include(driver);
            }
            catch (InvalidOperationException conflict)
            {
                LogActivatedAddInNotRegistered(logger, loaded.Key, conflict);
                continue;
            }

            activations.Include(hash);
            catalog.Activated(hash, loaded, driver);
            loadedCount++;
        }

        return loadedCount;
    }

    /// <summary>Revokes the activation of one add-in.</summary>
    /// <param name="contentHash">The bytes whose activation is being withdrawn.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>Whether a row was removed.</returns>
    public async Task<bool> RevokeAsync(string contentHash, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var removed = await db.ProviderAddInActivations
            .Where(row => row.ContentHash == contentHash)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        if (removed == 0)
        {
            return false;
        }

        activations.Exclude(contentHash);
        LogAddInRevoked(logger, contentHash);

        return true;
    }

    /// <summary>The activations this installation holds, newest first.</summary>
    /// <param name="ct">Cancels the read.</param>
    public async Task<IReadOnlyList<ProviderAddInActivationRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await db.ProviderAddInActivations
            .AsNoTracking()
            .OrderByDescending(row => row.ActivatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    // The attribute is read without running the add-in and the declaration is read by running it, so the two
    // can disagree. The administrator decided on the first.

    private static async Task<string?> HashOnDiskAsync(string filePath, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);

            return Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Activated the provider add-in {Key} {Version} from '{Path}' for {Administrator}")]
    private static partial void LogAddInActivated(
        ILogger logger,
        string key,
        string version,
        string path,
        string administrator);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The activated provider add-in at '{Path}' was not loaded on this start: {Reason}")]
    private static partial void LogActivatedAddInNotLoaded(ILogger logger, string path, string reason);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The provider add-in {Key} was activated and this host could not register it; the activation stands and the next start reports it")]
    private static partial void LogActivatedAddInNotRegistered(ILogger logger, string key, Exception conflict);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Revoked the activation of the provider add-in with content hash {ContentHash}; it stays loaded until this host restarts")]
    private static partial void LogAddInRevoked(ILogger logger, string contentHash);
}
