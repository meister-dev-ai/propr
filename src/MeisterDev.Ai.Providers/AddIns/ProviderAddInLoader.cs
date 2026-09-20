// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using System.Security.Cryptography;
using MeisterDev.Ai.Providers.Conformance;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using Microsoft.Extensions.Logging;

namespace MeisterDev.Ai.Providers.AddIns;

/// <summary>
///     Reads the two add-in directories once and reports every assembly it found, as a family it took or as a
///     skip with a category and a reason.
/// </summary>
/// <remarks>
///     <para>
///         Nothing here throws on a bad assembly. One unreadable file in a mounted directory would otherwise
///         take the whole product down, including every family that loaded, so a failure is recorded and the
///         pass continues. What that costs is visibility, which the recorded inventory pays back.
///     </para>
///     <para>
///         The pass runs once while the host starts. The directories are not watched and not rescanned, so
///         adding or removing a file under a running host changes nothing until it restarts.
///     </para>
///     <para>
///         An assembly that loads is then measured against the shared driver checks, and a family that fails one
///         is skipped with the check named. The checks are the same ones an add-in author runs in their own
///         build, so a family that was checked there arrives here with nothing left to find; a family that was
///         not is caught before it serves a review rather than during one.
///     </para>
///     <para>
///         Loading code from a directory is not sandboxed and is not intended to be. An add-in runs with the
///         host's full authority, and what an operator trusts when they put a file there is the file's author.
///         What the loader adds is accountability: it records the path and the content hash of every assembly it
///         chose to read, and it chooses only from inside the directory it was pointed at. An add-in's own
///         dependencies are a separate question, answered in <see cref="ProviderAddInLoadContext" />: the layout
///         its <c>.deps.json</c> declares is followed wherever it points, and a path outside the add-in's folder
///         is logged rather than refused.
///     </para>
/// </remarks>
public static partial class ProviderAddInLoader
{
    /// <summary>The directories in the order they are read, which is also the order precedence follows.</summary>
    private static readonly ProviderAddInOrigin[] ReadOrder =
        [ProviderAddInOrigin.BuiltIn, ProviderAddInOrigin.External];

    /// <summary>Reads both directories and builds the catalog.</summary>
    /// <param name="directories">The two directories to read, built-in first.</param>
    /// <param name="hostRegisteredDrivers">
    ///     The families compiled into the host. They already hold their identities, so an add-in declaring one of
    ///     them is recorded as a duplicate instead of reaching the registry, where a second driver for one family
    ///     stops the host starting.
    /// </param>
    /// <param name="activations">
    ///     Which add-ins an administrator has activated. An external add-in that is not among them is described
    ///     from its file and never loaded, so none of its code runs. Null gates nothing, which is what a caller
    ///     with no administrator to ask has.
    /// </param>
    /// <param name="logger">Where each load and each skip is reported.</param>
    public static ProviderAddInCatalog Load(
        ProviderAddInDirectories directories,
        IEnumerable<IAiProviderDriver> hostRegisteredDrivers,
        ILogger logger,
        IProviderAddInActivations? activations = null)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(hostRegisteredDrivers);
        ArgumentNullException.ThrowIfNull(logger);

        var gate = activations ?? ProviderAddInsAllActivated.Instance;
        var loaded = new List<LoadedProviderAddIn>();
        var rejected = new List<RejectedProviderAddIn>();
        var drivers = new List<IAiProviderDriver>();
        var awaiting = new List<DiscoveredProviderAddIn>();
        var claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var driver in hostRegisteredDrivers)
        {
            claims[IdentityOf(driver).Value] = $"the driver '{driver.GetType().Name}' built into the host";
        }

        foreach (var origin in ReadOrder)
        {
            var directory = directories.For(origin);
            var candidates = Discover(directory, origin, rejected, logger);

            var accepted = new List<Accepted>();
            foreach (var candidate in candidates)
            {
                // The gate is read before anything is loaded, so an add-in nobody activated has none of its
                // code run: not a module initializer, not a static constructor, not a driver. What an
                // administrator is shown instead is read out of the file by the metadata reader.
                if (origin == ProviderAddInOrigin.External && Held(candidate, gate, claims, logger) is { } held)
                {
                    awaiting.Add(held);
                    LogAddInAwaitingActivation(logger, held.FilePath, held.Key ?? held.AssemblyName);
                    continue;
                }

                if (Examine(candidate, rejected, logger) is { } examined)
                {
                    accepted.Add(examined);
                }
            }

            foreach (var survivor in KeepOnlyUncontested(accepted, rejected, logger))
            {
                Claim(survivor, claims, loaded, drivers, rejected, logger);
            }
        }

        return new ProviderAddInCatalog(loaded, rejected, drivers, awaiting);
    }

    /// <summary>
    ///     Loads one add-in an administrator has just activated.
    /// </summary>
    /// <param name="assemblyPath">The assembly to load.</param>
    /// <param name="folderPath">The folder it sits in.</param>
    /// <param name="origin">Which directory it came from.</param>
    /// <param name="logger">Where the load or the refusal is reported.</param>
    /// <returns>What was loaded, or null with the reason it was not.</returns>
    /// <remarks>
    ///     The same examination the discovery pass makes, for one file. An activation happens while the host is
    ///     serving, so the outcome is handed back rather than collected into a catalog: the caller decides what
    ///     to do with a family that turns out not to load, and the administrator who pressed the button is the
    ///     one who reads the reason.
    /// </remarks>
    public static (LoadedProviderAddIn? Loaded, IAiProviderDriver? Driver, string? Refusal) LoadOne(
        string assemblyPath,
        string folderPath,
        ProviderAddInOrigin origin,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentNullException.ThrowIfNull(logger);

        var rejected = new List<RejectedProviderAddIn>();

        if (Examine(new Candidate(assemblyPath, folderPath, origin), rejected, logger) is not { } accepted)
        {
            return (null, null, rejected.FirstOrDefault()?.Reason ?? "The add-in could not be examined.");
        }

        return (
            new LoadedProviderAddIn(
                accepted.Declaration.Key,
                accepted.Declaration.Label,
                accepted.Declaration.Version,
                accepted.Declaration.ContractVersion,
                accepted.Declaration.ReachedHostPatterns,
                accepted.Declaration.RequiredCapabilityKey,
                assemblyPath,
                accepted.ContentHash,
                origin),
            accepted.Driver,
            null);
    }

    /// <summary>
    ///     Describes an add-in an administrator has not activated, or returns null when they have.
    /// </summary>
    /// <remarks>
    ///     Reads the file twice and runs none of it: once for the bytes, to hash them, and once through a
    ///     metadata-only context, to read the manifest the add-in states about itself. An add-in whose hash
    ///     cannot be read, whose manifest is absent, or whose manifest names another contract is described with
    ///     the reason and cannot be activated as it stands.
    /// </remarks>
    private static DiscoveredProviderAddIn? Held(
        Candidate candidate,
        IProviderAddInActivations gate,
        IReadOnlyDictionary<string, string> claims,
        ILogger logger)
    {
        var contentHash = TryHash(candidate.AssemblyPath);

        if (gate.IsActivated(contentHash))
        {
            return null;
        }

        var (manifest, unreadable) = ProviderAddInManifestReader.Read(
            candidate.AssemblyPath,
            candidate.FolderPath,
            logger);

        return new DiscoveredProviderAddIn(
            candidate.AssemblyPath,
            contentHash,
            candidate.Origin,
            manifest?.Key,
            manifest?.Label,
            manifest?.Version,
            manifest?.ContractVersion,
            manifest?.ReachedHosts ?? [],
            manifest?.RequiredCapability,
            manifest?.AssemblyName ?? Path.GetFileNameWithoutExtension(candidate.AssemblyPath),
            manifest?.AssemblyVersion ?? "0.0.0.0",
            RefusalFor(contentHash, manifest, unreadable, claims));
    }

    // What stops an administrator activating this add-in as it stands. Each is decidable from the file, so it is
    // said before the decision rather than after the load.
    private static string? RefusalFor(
        string? contentHash,
        ProviderAddInManifest? manifest,
        string? unreadable,
        IReadOnlyDictionary<string, string> claims)
    {
        if (contentHash is null)
        {
            return "The file could not be read, so there is nothing to bind an activation to. Check that the "
                   + "host can read it and that nothing is writing to it.";
        }

        if (unreadable is not null)
        {
            return unreadable;
        }

        if (manifest is null)
        {
            return $"The assembly states no '{nameof(ProviderAddInAttribute)}', so it says nothing about what it "
                   + "is or which hosts it contacts. An add-in declares one at assembly level; the contract's "
                   + "README shows the form.";
        }

        if (!string.Equals(manifest.ContractVersion, ProviderContract.Version, StringComparison.Ordinal))
        {
            var stated = string.IsNullOrWhiteSpace(manifest.ContractVersion)
                ? "no contract version"
                : $"contract version '{manifest.ContractVersion}'";

            return $"The add-in states {stated} and this host carries '{ProviderContract.Version}'. Rebuild it "
                   + "against this host's contract package.";
        }

        if (ProviderVocabulary.ValidateKey(manifest.Key) is { } refusal)
        {
            return $"The identity key it states cannot be stored against a connection. {refusal.Message}";
        }

        // Said before the decision, because activating it would refuse on the same grounds and the administrator
        // would have learned nothing they could not have been told here.
        if (claims.TryGetValue(manifest.Key, out var holder))
        {
            return $"The identity '{manifest.Key}' is already served by {holder}. A connection is stored against "
                   + "that key, so it names one family.";
        }

        return null;
    }

    /// <summary>Lists the assemblies in one directory that are worth trying to load.</summary>
    /// <remarks>
    ///     An add-in occupies its own folder, named after its assembly, so its dependencies sit beside it and no
    ///     two families share a probing folder. Everything else in the directory is left alone, except a loose
    ///     assembly, which is recorded so an operator who dropped one straight into the directory is told why
    ///     nothing happened rather than left looking for a family that never appeared.
    /// </remarks>
    private static List<Candidate> Discover(
        string directoryPath,
        ProviderAddInOrigin origin,
        List<RejectedProviderAddIn> rejected,
        ILogger logger)
    {
        var candidates = new List<Candidate>();

        if (!Directory.Exists(directoryPath))
        {
            // Reported once and not a failure: a stock deployment configures no external directory, and a host
            // built without add-ins has no built-in one either.
            LogAddInDirectoryAbsent(logger, directoryPath, ProviderAddInNames.Format(origin));
            return candidates;
        }

        List<string> folders;
        List<string> looseAssemblies;
        try
        {
            folders = [.. Directory.EnumerateDirectories(directoryPath).Order(StringComparer.Ordinal)];
            looseAssemblies = [.. Directory.EnumerateFiles(directoryPath, "*.dll").Order(StringComparer.Ordinal)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogAddInDirectoryUnreadable(logger, directoryPath, ProviderAddInNames.Format(origin), exception);
            return candidates;
        }

        foreach (var folder in folders)
        {
            var assemblyPath = Path.Combine(folder, Path.GetFileName(folder) + ".dll");

            if (ProviderAddInContainment.GetContainmentFailureReason(directoryPath, folder, isDirectory: true) is { } folderEscape)
            {
                rejected.Add(Reject(ProviderAddInRejectionCategory.Failed, folderEscape, folder, origin, key: null, logger));
                continue;
            }

            if (!File.Exists(assemblyPath))
            {
                rejected.Add(
                    Reject(
                        ProviderAddInRejectionCategory.Failed,
                        $"The folder '{folder}' holds no '{Path.GetFileName(assemblyPath)}'. An add-in's folder is "
                        + "named after the assembly in it.",
                        assemblyPath,
                        origin,
                        key: null,
                        logger));
                continue;
            }

            if (ProviderAddInContainment.GetContainmentFailureReason(directoryPath, assemblyPath, isDirectory: false)
                is { } assemblyEscape)
            {
                rejected.Add(
                    Reject(
                        ProviderAddInRejectionCategory.Failed,
                        assemblyEscape,
                        assemblyPath,
                        origin,
                        key: null,
                        logger));
                continue;
            }

            candidates.Add(new Candidate(assemblyPath, folder, origin));
        }

        foreach (var loose in looseAssemblies)
        {
            rejected.Add(
                Reject(
                    ProviderAddInRejectionCategory.Failed,
                    $"'{loose}' sits directly in the add-in directory. An add-in occupies its own folder, named "
                    + "after the assembly, so its dependencies resolve from beside it and no two families share a "
                    + "folder.",
                    loose,
                    origin,
                    key: null,
                    logger));
        }

        return candidates;
    }

    /// <summary>Loads one candidate and reads what the host needs before it can be registered.</summary>
    /// <remarks>
    ///     The file is opened once and kept open across the load, so the hash recorded in the inventory and the
    ///     bytes the runtime maps come from the same file. Hashing through one handle and loading through another
    ///     records a hash of whatever was at the path when it was read, which is not necessarily what is running.
    /// </remarks>
    private static Accepted? Examine(Candidate candidate, List<RejectedProviderAddIn> rejected, ILogger logger)
    {
        // Read once, into memory. The hash below and the assembly loaded further down both come from this one
        // array, so the bytes an administrator approves are the bytes that run. Loading by path instead would
        // reopen the file: a rename over the path between the hash and the load would run code the hash never
        // covered, and a module initializer in it runs before any later comparison could refuse it.
        var image = TryRead(candidate.AssemblyPath);
        var contentHash = image is null ? null : Hash(image);

        // Before the assembly is loaded, so a copy of a shared assembly is never resolved into an add-in's
        // context. Once it has been, the family's driver implements a different interface than the host's and
        // nothing about the load reports it.
        var shippedCopies = ProviderAddInSharedAssemblies.FindShippedCopies(candidate.FolderPath);
        if (shippedCopies.Count > 0)
        {
            rejected.Add(
                Reject(
                    ProviderAddInRejectionCategory.MisPackaged,
                    $"The folder holds {string.Join(" and ", shippedCopies.Select(copy => $"'{copy}'"))}. The host "
                    + "and every add-in resolve those from the host's own load context, so an add-in that ships a "
                    + "copy runs against a different contract than the host holds. Rebuild the add-in with the "
                    + "shared add-in build configuration.",
                    candidate.AssemblyPath,
                    candidate.Origin,
                    key: null,
                    logger));
            return null;
        }

        try
        {
            var context = new ProviderAddInLoadContext(candidate.AssemblyPath, logger);

            // From the bytes already read and hashed, not from the path. The context still resolves this
            // add-in's dependencies by path, which is a separate decision the activation gate does not cover.
            using var image_ = new MemoryStream(image!, writable: false);
            var assembly = context.LoadFromStream(image_);

            var driverTypes = assembly
                .GetExportedTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                               && typeof(IAiProviderDriver).IsAssignableFrom(type))
                .ToList();

            if (driverTypes.Count == 0)
            {
                rejected.Add(
                    Reject(
                        ProviderAddInRejectionCategory.Failed,
                        $"The assembly exposes no public '{nameof(IAiProviderDriver)}'. An add-in that ships its own "
                        + "copy of the contract exposes none either, because its driver then implements a different "
                        + "interface than the host's.",
                        candidate.AssemblyPath,
                        candidate.Origin,
                        key: null,
                        logger));
                return null;
            }

            if (driverTypes.Count > 1)
            {
                rejected.Add(
                    Reject(
                        ProviderAddInRejectionCategory.Failed,
                        $"The assembly exposes {driverTypes.Count} drivers "
                        + $"({string.Join(", ", driverTypes.Select(type => $"'{type.FullName}'"))}). An add-in "
                        + "supplies one provider family, because one file, one identity and one content hash are "
                        + "what the inventory reports.",
                        candidate.AssemblyPath,
                        candidate.Origin,
                        key: null,
                        logger));
                return null;
            }

            var driverType = driverTypes[0];
            if (driverType.GetConstructor(Type.EmptyTypes) is null)
            {
                rejected.Add(
                    Reject(
                        ProviderAddInRejectionCategory.Failed,
                        $"'{driverType.FullName}' has no public parameterless constructor. The loader constructs a "
                        + "driver itself; what a family needs at runtime comes from the host primitives the "
                        + "contract hands it.",
                        candidate.AssemblyPath,
                        candidate.Origin,
                        key: null,
                        logger));
                return null;
            }

            var driver = (IAiProviderDriver)Activator.CreateInstance(driverType)!;
            var declaration = driver.Declaration
                              ?? throw new InvalidOperationException($"'{driverType.FullName}' supplies no declaration.");

            // The contract version is compared before any other member of the declaration is read, so a family
            // built against an incompatible contract cannot fail on a member the host expects to be there. That
            // is also why nothing below records the declared key: reading it would be reading the declaration.
            var declaredContractVersion = declaration.ContractVersion;
            if (!string.Equals(declaredContractVersion, ProviderContract.Version, StringComparison.Ordinal))
            {
                var declared = string.IsNullOrWhiteSpace(declaredContractVersion)
                    ? "no contract version"
                    : $"contract version '{declaredContractVersion}'";

                rejected.Add(
                    Reject(
                        ProviderAddInRejectionCategory.VersionMismatch,
                        $"The family declares {declared}; this host carries '{ProviderContract.Version}'. The "
                        + "contract has no compatibility guarantee, so a family is rebuilt against the version the "
                        + "host it runs on carries.",
                        candidate.AssemblyPath,
                        candidate.Origin,
                        key: null,
                        logger));
                return null;
            }

            // The declaration checks, run against every family the host takes. They read what the family says
            // about itself and replay the usage payload it recorded; none of them resolves a name or reaches the
            // network. A family that fails one has an authoring mistake that would otherwise show up as a broken
            // form or a mispriced review.
            var report = DriverConformance.Run(new ConformanceSubject(driver));
            if (!report.Passed)
            {
                rejected.Add(
                    Reject(
                        ProviderAddInRejectionCategory.NonConforming,
                        $"The family fails the shared driver checks. {report.Summary}",
                        candidate.AssemblyPath,
                        candidate.Origin,
                        declaration.Key,
                        logger));
                return null;
            }

            return new Accepted(candidate, driver, declaration, contentHash);
        }
        catch (Exception exception)
        {
            // Every exception, because this is the point where code the host did not compile runs for the first
            // time. Narrowing it would leave whichever kind was not named taking the host down at startup.
            rejected.Add(
                Reject(
                    ProviderAddInRejectionCategory.Failed,
                    Describe(exception),
                    candidate.AssemblyPath,
                    candidate.Origin,
                    key: null,
                    logger));
            return null;
        }
    }

    /// <summary>Skips every add-in in one directory that shares an identity with another in the same directory.</summary>
    /// <remarks>
    ///     Both are skipped, not one: neither copy has precedence over the other inside one directory, and
    ///     resolving it by enumeration order would serve whichever the filesystem happened to list first.
    /// </remarks>
    private static List<Accepted> KeepOnlyUncontested(
        List<Accepted> accepted,
        List<RejectedProviderAddIn> rejected,
        ILogger logger)
    {
        var byIdentity = new Dictionary<string, List<Accepted>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in accepted)
        {
            var identity = IdentityOf(item.Driver);
            if (!byIdentity.TryGetValue(identity.Value, out var sharing))
            {
                sharing = [];
                byIdentity[identity.Value] = sharing;
            }

            sharing.Add(item);
        }

        var survivors = new List<Accepted>();
        foreach (var item in accepted)
        {
            var identity = IdentityOf(item.Driver);
            var sharing = byIdentity[identity.Value];

            if (sharing.Count == 1)
            {
                survivors.Add(item);
                continue;
            }

            var rivals = sharing
                .Where(other => !ReferenceEquals(other, item))
                .Select(other => $"'{other.Candidate.AssemblyPath}'")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);

            rejected.Add(
                Reject(
                    ProviderAddInRejectionCategory.Duplicate,
                    $"{identity.Description} is also declared by {string.Join(", ", rivals)}. Neither copy has "
                    + "precedence inside one directory, so both are skipped.",
                    item.Candidate.AssemblyPath,
                    item.Candidate.Origin,
                    item.Declaration.Key,
                    logger));
        }

        return survivors;
    }

    /// <summary>Takes an add-in whose identity is still free, and records a duplicate when it is not.</summary>
    private static void Claim(
        Accepted accepted,
        Dictionary<string, string> claims,
        List<LoadedProviderAddIn> loaded,
        List<IAiProviderDriver> drivers,
        List<RejectedProviderAddIn> rejected,
        ILogger logger)
    {
        var identity = IdentityOf(accepted.Driver);

        if (claims.TryGetValue(identity.Value, out var holder))
        {
            rejected.Add(
                Reject(
                    ProviderAddInRejectionCategory.Duplicate,
                    $"{identity.Description} is already served by {holder}. A family the deployment ships is "
                    + "changed by a product release and not by a copy in the external directory.",
                    accepted.Candidate.AssemblyPath,
                    accepted.Candidate.Origin,
                    accepted.Declaration.Key,
                    logger));
            return;
        }

        claims[identity.Value] = $"'{accepted.Candidate.AssemblyPath}'";

        var declaration = accepted.Declaration;
        loaded.Add(
            new LoadedProviderAddIn(
                declaration.Key,
                declaration.Label,
                declaration.Version,
                declaration.ContractVersion,
                declaration.ReachedHostPatterns,
                declaration.RequiredCapabilityKey,
                accepted.Candidate.AssemblyPath,
                accepted.ContentHash,
                accepted.Candidate.Origin));
        drivers.Add(accepted.Driver);

        LogAddInLoaded(
            logger,
            declaration.Key,
            declaration.Label,
            declaration.Version,
            accepted.Candidate.AssemblyPath,
            ProviderAddInNames.Format(accepted.Candidate.Origin));
    }

    /// <summary>The identity one driver claims, which is the family a connection is stored against.</summary>
    /// <remarks>
    ///     A driver states its family once, as its declared key, and the registry indexes that key. A second
    ///     driver for one key stops the host starting, so an add-in claiming a key another driver already serves
    ///     is skipped here instead.
    /// </remarks>
    private static Identity IdentityOf(IAiProviderDriver driver)
    {
        var key = driver.Declaration.Key;

        return new Identity(key, $"The identity key '{key}'");
    }

    private static RejectedProviderAddIn Reject(
        ProviderAddInRejectionCategory category,
        string reason,
        string filePath,
        ProviderAddInOrigin origin,
        string? key,
        ILogger logger)
    {
        var rejection = new RejectedProviderAddIn(
            category,
            reason,
            filePath,
            TryHash(filePath),
            key,
            origin);

        LogAddInSkipped(
            logger,
            ProviderAddInNames.Format(category),
            filePath,
            reason,
            ProviderAddInNames.Format(origin));

        return rejection;
    }

    /// <summary>What an exception from an add-in says, including the detail its own message leaves out.</summary>
    private static string Describe(Exception exception)
    {
        return exception switch
        {
            ReflectionTypeLoadException typeLoad =>
                $"{typeLoad.Message} {string.Join(
                    " ",
                    typeLoad.LoaderExceptions.Where(inner => inner is not null).Select(inner => inner!.Message))}"
                    .TrimEnd(),
            TypeInitializationException or TargetInvocationException when exception.InnerException is { } inner =>
                $"{exception.Message} {inner.Message}",
            _ => exception.Message,
        };
    }

    private static string? TryHash(string filePath)
    {
        var image = TryRead(filePath);

        return image is null ? null : Hash(image);
    }

    private static string Hash(byte[] image)
    {
        return Convert.ToHexStringLower(SHA256.HashData(image));
    }

    /// <summary>The whole file as bytes, or null when the host cannot read it.</summary>
    /// <remarks>
    ///     A file the host cannot read is still recorded, with no hash, because the path is the part an operator
    ///     needs in order to find it. Reading it whole is what lets the hash and the load see the same bytes; an
    ///     add-in assembly is small enough that holding it briefly costs nothing worth measuring.
    /// </remarks>
    private static byte[]? TryRead(string filePath)
    {
        try
        {
            return File.ReadAllBytes(filePath);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed record Candidate(string AssemblyPath, string FolderPath, ProviderAddInOrigin Origin);

    private sealed record Accepted(
        Candidate Candidate,
        IAiProviderDriver Driver,
        ProviderDeclaration Declaration,
        string? ContentHash);

    private sealed record Identity(string Value, string Description);
}
