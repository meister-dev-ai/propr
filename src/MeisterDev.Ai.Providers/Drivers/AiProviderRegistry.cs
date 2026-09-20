// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Default in-memory provider driver registry backed by dependency injection.
/// </summary>
/// <remarks>
///     Composition decides what exists: the registry indexes whatever drivers were registered, so a family the
///     host has never heard of is available once a driver declaring it is loaded, and nothing has to be changed
///     here. Two drivers claiming one identity are rejected while the registry is built, because the registry has
///     no basis for choosing between them. A driver that declares no authentication mode is rejected there too,
///     because no profile could be configured against it.
///     <para>
///         The index is keyed by the identity a family declares and by every identity it supersedes, so a
///         connection stored under a spelling a family has since replaced still reaches that family's driver. The
///         declared key is what a lookup answers with, which 
///         <see cref="AiProviderLegacyNames.ResolveIdentity" /> reports.
///     </para>
/// </remarks>
public sealed class AiProviderRegistry : IAiProviderDriverRegistry
{
    private readonly Lock _gate = new();
    private List<IAiProviderDriver> _registered;
    private IReadOnlyDictionary<string, IAiProviderDriver> _drivers;

    /// <summary>Indexes the drivers a composition supplies.</summary>
    /// <param name="drivers">The drivers, in registration order.</param>
    public AiProviderRegistry(IEnumerable<IAiProviderDriver> drivers)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        this._registered = [.. drivers];
        this._drivers = IndexByFamily(this._registered);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> RegisteredKinds =>
    [
        .. this.Index.Values
            .Select(driver => driver.Declaration.Key)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    // Read through a local, so a lookup and the rebuild an activation causes never see a half-built map. The
    // reference is swapped under the lock and every reader takes the one that was current when it started.
    private IReadOnlyDictionary<string, IAiProviderDriver> Index => Volatile.Read(ref this._drivers);

    /// <inheritdoc />
    public bool IsRegistered(string? providerKind)
    {
        return providerKind is not null && this.Index.ContainsKey(providerKind.Trim());
    }

    /// <inheritdoc />
    public IAiProviderDriver GetRequired(string? providerKind)
    {
        return providerKind is not null && this.Index.TryGetValue(providerKind.Trim(), out var driver)
            ? driver
            // Names the family and what is available, because the usual cause is a profile configured against a
            // build that has the driver and then run against one that does not.
            : throw new InvalidOperationException(
                $"No AI provider driver is registered for '{providerKind}' in this build "
                + $"(available: {string.Join(", ", this.RegisteredKinds)}).");
    }

    /// <summary>Adds a driver an administrator has just activated.</summary>
    /// <param name="driver">The driver the activated add-in supplied.</param>
    /// <exception cref="InvalidOperationException">
    ///     The driver claims an identity another one already holds, or declares nothing it could be configured
    ///     with. The registry is left as it was.
    /// </exception>
    /// <remarks>
    ///     The whole index is rebuilt rather than extended, so an activation is held to every rule a composition
    ///     is: the duplicate-identity check, the superseded spellings, and the authentication modes. Rebuilding
    ///     costs one pass over a handful of drivers and happens when an administrator clicks Activate.
    /// </remarks>
    public void Include(IAiProviderDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);

        lock (this._gate)
        {
            var extended = new List<IAiProviderDriver>(this._registered) { driver };

            // Built before either field is written, so a rejected driver leaves the registry serving exactly
            // what it served before.
            var index = IndexByFamily(extended);

            this._registered = extended;
            Volatile.Write(ref this._drivers, index);
        }
    }

    /// <summary>
    ///     Indexes the drivers by every identity each one claims, and rejects an identity claimed by more than
    ///     one driver or a driver that declares no authentication mode.
    /// </summary>
    /// <param name="drivers">The registered drivers, in registration order.</param>
    /// <returns>The drivers keyed by each identity they claim: the declared key, and every key superseded.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Two drivers claim the same identity, or a driver declares no authentication mode.
    /// </exception>
    /// <remarks>
    ///     Keeping either of two drivers would leave the family served by one nobody selected, with nothing
    ///     recorded to say the other was displaced. The message names the identity and both types so the duplicate
    ///     registration can be found without a debugger.
    /// </remarks>
    private static Dictionary<string, IAiProviderDriver> IndexByFamily(IEnumerable<IAiProviderDriver> drivers)
    {
        // Case-insensitive, as keys are compared everywhere else: two identities differing only in case name one
        // family, and indexing them apart would serve whichever casing a caller happened to use.
        var claimed = new Dictionary<string, IAiProviderDriver>(StringComparer.OrdinalIgnoreCase);

        foreach (var driver in drivers)
        {
            if (driver.Declaration is not { } declaration)
            {
                throw new InvalidOperationException(
                    $"The AI provider driver '{driver.GetType().FullName}' supplies no declaration. A driver "
                    + "declares what its family is before a host can offer it.");
            }

            // The declared key is the identity a connection is stored against. A second family claiming a key
            // already claimed would take over every connection stored against it.
            if (claimed.TryGetValue(declaration.Key, out var claimant))
            {
                throw new InvalidOperationException(
                    $"Two AI provider drivers claim the identity '{declaration.Key}': "
                    + $"'{claimant.GetType().FullName}' and '{driver.GetType().FullName}'. "
                    + "The key is what a connection is stored against, so it names one family.");
            }

            claimed.Add(declaration.Key, driver);

            // The keys a family supersedes are checked against the same map. A stored identity is the only thing
            // that says which family a connection belongs to, so a spelling two families both claim leaves the
            // host no way to decide what such a row means. The second family is refused rather than silently
            // losing the first family's connections to it.
            foreach (var legacyKey in declaration.LegacyNames.Keys)
            {
                if (claimed.TryGetValue(legacyKey, out var holder) && !ReferenceEquals(holder, driver))
                {
                    throw new InvalidOperationException(
                        $"Two AI provider drivers claim the identity '{legacyKey}': "
                        + $"'{holder.Declaration.Key}' ('{holder.GetType().FullName}') and "
                        + $"'{declaration.Key}' ('{driver.GetType().FullName}'). "
                        + "A stored identity names one family, so a superseded spelling belongs to one family "
                        + "as well.");
                }

                claimed[legacyKey] = driver;
            }

            // A family with no authentication mode cannot be configured: every mode an operator could pick would be
            // one the driver does not read. Rejected where the composition is built rather than left to produce
            // a family that is offered and then refuses every profile saved against it.
            if (driver.SupportedAuthModes is not { Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"The AI provider driver '{driver.GetType().FullName}' for '{declaration.Key}' declares "
                    + "no authentication mode. A driver must declare at least one authentication mode its family "
                    + "can authenticate with.");
            }

            // A mode with no field declaration is a mode nothing is collected for: the operator is offered it,
            // enters nothing, and the profile is saved without a credential. Rejected here for the same reason
            // as the empty mode set, because both produce a family that is offered and cannot be configured. A
            // mode present as a key with no list behind it counts as undeclared: reading it hands a null list to
            // every caller that collects or checks the fields.
            var undeclared = driver.SupportedAuthModes
                .Where(mode => driver.CredentialFields is null
                               || !driver.CredentialFields.TryGetValue(mode, out var fields)
                               || fields is null)
                .ToList();
            if (undeclared.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The AI provider driver '{driver.GetType().FullName}' for '{declaration.Key}' declares "
                    + $"the authentication mode(s) {string.Join(", ", undeclared)} without declaring the "
                    + "credential fields they need. Declare the fields, or an empty list for a mode that needs "
                    + "no stored credential.");
            }
        }

        return claimed;
    }
}
