// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Licensing;

namespace MeisterDev.ProPR.Application.Features.Licensing.Services;

/// <summary>
///     Calculates effective premium capability availability from the activated license, the installation's
///     capability overrides, and the static catalog.
///     <para>
///         The license is the source of entitlement: a capability that requires a commercial license is available
///         only while a verified license whose term is running names its key. The stored overrides only take a
///         licensed capability away; there is no override that grants one. The edition follows from the license
///         state rather than being configured separately, so nothing can report an edition the license does not
///         support.
///     </para>
///     <para>
///         The license state provider loads and verifies the stored document at most once per its cache window,
///         so a per-capability check does not repeat that work. The capability overrides are read from the policy
///         store on every check. Nothing is cached here on top of either.
///     </para>
/// </summary>
public sealed class LicensingCapabilityService(
    IPremiumCapabilityCatalog capabilityCatalog,
    ILicensingPolicyStore policyStore,
    ILicenseStateProvider licenseStateProvider,
    ILicensingIdentityStore licensingIdentityStore) : ILicensingCapabilityService
{
    // The two messages below describe the installation's license rather than the capability, which is why they
    // are written once here instead of per catalog entry: the term is installation-wide, so naming the
    // capability would add nothing an operator can act on.
    private const string LicenseRevertedMessage =
        "This installation's license expired and its grace window ended. Activate a renewed license to make commercial capabilities available again.";

    private const string LicenseNotYetValidMessage =
        "This installation's license term has not started yet, so the capabilities it grants are not available.";

    /// <summary>Gets a summary of the current licensing configuration and all premium capabilities.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A licensing summary containing the edition, activation date, and capability snapshots.</returns>
    public async Task<LicensingSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var licenseState = await licenseStateProvider.GetStateAsync(cancellationToken);
        var policy = await policyStore.GetAsync(cancellationToken);
        var licensingIdentity = await licensingIdentityStore.GetOrCreateAsync(cancellationToken);

        return BuildSummary(licenseState, policy, capabilityCatalog, licensingIdentity);
    }

    /// <summary>Gets authentication options based on the current licensing configuration.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An authentication options DTO containing the edition and available sign-in methods.</returns>
    public async Task<AuthOptionsDto> GetAuthOptionsAsync(CancellationToken cancellationToken = default)
    {
        var summary = await this.GetSummaryAsync(cancellationToken);
        var signInMethods = new List<string> { "password" };

        var ssoCapability = summary.Capabilities.FirstOrDefault(capability =>
            string.Equals(capability.Key, PremiumCapabilityKey.SsoAuthentication, StringComparison.OrdinalIgnoreCase));
        if (ssoCapability?.IsAvailable == true)
        {
            signInMethods.Add("sso");
        }

        return new AuthOptionsDto(summary.Edition, signInMethods.AsReadOnly(), summary.Capabilities);
    }

    /// <summary>Gets the current snapshot of a specific premium capability.</summary>
    /// <param name="capabilityKey">The unique key of the premium capability.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A capability snapshot containing the current state and availability of the specified capability.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the capability key is not found in the catalog.</exception>
    public async Task<CapabilitySnapshot> GetCapabilityAsync(
        string capabilityKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);

        var definition = capabilityCatalog.Get(capabilityKey)
                         ?? throw new KeyNotFoundException($"Unknown premium capability '{capabilityKey}'.");
        var licenseState = await licenseStateProvider.GetStateAsync(cancellationToken);
        var policy = await policyStore.GetAsync(cancellationToken);

        return ResolveSnapshot(definition, licenseState, policy);
    }

    /// <summary>Determines whether a specific premium capability is enabled.</summary>
    /// <param name="capabilityKey">The unique key of the premium capability.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the capability is available; otherwise, false.</returns>
    public async ValueTask<bool> IsEnabledAsync(string capabilityKey, CancellationToken cancellationToken = default)
    {
        return (await this.GetCapabilityAsync(capabilityKey, cancellationToken)).IsAvailable;
    }

    /// <summary>Updates the premium capability overrides.</summary>
    /// <param name="capabilityOverrides">A collection of capability override mutations to apply.</param>
    /// <param name="actorUserId">The ID of the user performing the update, if any.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A licensing summary with the updated configuration and capability snapshots.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when an override names a capability the catalog does not carry.</exception>
    public async Task<LicensingSummaryDto> UpdateAsync(
        IReadOnlyCollection<CapabilityOverrideMutation> capabilityOverrides,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilityOverrides);

        var licenseState = await licenseStateProvider.GetStateAsync(cancellationToken);
        var overrideList = capabilityOverrides.ToList();

        // Every key is checked before anything is written, so a batch naming one capability that does not exist
        // leaves the stored overrides as they were. Nothing else is checked here: an override state can only
        // take a capability away, so no state has to be refused against the license.
        foreach (var overrideMutation in overrideList)
        {
            if (capabilityCatalog.Get(overrideMutation.Key) is null)
            {
                throw new KeyNotFoundException($"Unknown premium capability '{overrideMutation.Key}'.");
            }
        }

        var updatedPolicy = await policyStore.UpdateAsync(overrideList, actorUserId, cancellationToken);
        var licensingIdentity = await licensingIdentityStore.GetOrCreateAsync(cancellationToken);

        return BuildSummary(licenseState, updatedPolicy, capabilityCatalog, licensingIdentity);
    }

    private static LicensingSummaryDto BuildSummary(
        LicenseState licenseState,
        InstallationLicensingPolicy policy,
        IPremiumCapabilityCatalog catalog,
        Guid licensingIdentity)
    {
        var capabilities = catalog.GetAll()
            .Select(definition => ResolveSnapshot(definition, licenseState, policy))
            .Select(ToDto)
            .ToList()
            .AsReadOnly();

        // The activation instant is reported only while the license it belongs to is in force. A license outside
        // its term and its grace window leaves the installation on the community edition, and reporting when it
        // was activated next to that edition would read as an active commercial installation.
        var activatedAt = licenseState.Edition == InstallationEdition.Commercial ? licenseState.ActivatedAt : null;

        // The stage and its boundary instants are reported whatever the edition, because an installation that has
        // reverted needs to see when its term ended as much as one still inside it. Who the license was issued to,
        // its identifier and the limits it states are reported on the same rule: they describe the document on
        // file, which an operator arranging a renewal needs to read after the term has run out. The reporting
        // identifier is carried whatever the edition as well, because it identifies the installation and not its
        // entitlement.
        return new LicensingSummaryDto(
            licenseState.Edition,
            activatedAt,
            capabilities,
            licenseState.Stage,
            licenseState.NotBefore,
            licenseState.WarningStartsAt,
            licenseState.ExpiresAt,
            licenseState.GraceEndsAt,
            licenseState.DaysRemaining,
            licensingIdentity,
            licenseState.Claims?.Licensee,
            licenseState.Claims?.LicenseId,
            BuildLimits(licenseState.Claims?.Limits ?? LicenseLimits.None));
    }

    /// <summary>
    ///     Reports every limit rather than only the ones a license states, so the panel lists the same four
    ///     dimensions whatever document is on file and an absent limit is visible as absent.
    ///     <para>
    ///         Only what each limit states is reported here. The effective ceilings and the counts are left
    ///         unset: this summary is served to sign-in and session callers as well, and resolving ceilings for
    ///         them would take work none of them uses. This service also cannot resolve them, because the
    ///         resolver consults it. The admin read fills both in.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<LicenseLimitDto> BuildLimits(LicenseLimits limits)
    {
        return new List<LicenseLimitDto>
        {
            ToDto(LicenseLimitKey.AuthorsPerMonth, limits.AuthorsPerMonth),
            ToDto(LicenseLimitKey.Clients, limits.Clients),
            ToDto(LicenseLimitKey.Runners, limits.Runners),
            ToDto(LicenseLimitKey.ConcurrentReviews, limits.ConcurrentReviews),
        }.AsReadOnly();
    }

    private static LicenseLimitDto ToDto(LicenseLimitKey key, LicenseLimit limit)
    {
        if (limit.IsUnlimited)
        {
            return new LicenseLimitDto(key, LicenseLimitAllowance.Unlimited);
        }

        return limit.TryGetCount(out var licensedCount)
            ? new LicenseLimitDto(key, LicenseLimitAllowance.Count, licensedCount)
            : new LicenseLimitDto(key, LicenseLimitAllowance.Absent);
    }

    /// <summary>
    ///     Resolves one capability against the license and the installation's overrides.
    ///     <para>
    ///         A capability that requires a commercial license is available only when the license state amounts to
    ///         the commercial edition, its capability list names the key, and no disable-override applies. An
    ///         override the installation has stored can therefore only subtract from what the license grants.
    ///     </para>
    ///     <para>
    ///         A capability that requires no license is available unless an override disables it.
    ///     </para>
    ///     <para>
    ///         Whether a license amounts to the commercial edition is asked of the license state rather than
    ///         restated here, so a rule added to that mapping reaches capability resolution as well.
    ///     </para>
    /// </summary>
    private static CapabilitySnapshot ResolveSnapshot(
        PremiumCapabilityDefinition definition,
        LicenseState licenseState,
        InstallationLicensingPolicy policy)
    {
        var overrideState = policy.GetOverrideState(definition.Key);
        var isDisabledByOverride = overrideState == PremiumCapabilityOverrideState.Disabled;

        var reason = ResolveUnavailableReason(definition, licenseState, isDisabledByOverride);
        var message = reason switch
        {
            PremiumCapabilityUnavailableReason.NoLicense => definition.CommercialRequiredMessage,
            PremiumCapabilityUnavailableReason.NotInLicense => definition.NotInLicenseMessage,
            PremiumCapabilityUnavailableReason.DisabledByOverride => definition.CommercialDisabledMessage,
            PremiumCapabilityUnavailableReason.Reverted => LicenseRevertedMessage,
            PremiumCapabilityUnavailableReason.NotYetValid => LicenseNotYetValidMessage,
            _ => null,
        };

        return new CapabilitySnapshot(
            definition.Key,
            definition.DisplayName,
            definition.RequiresCommercial,
            overrideState,
            reason is null,
            message,
            reason);
    }

    /// <summary>
    ///     Returns why the capability is unavailable, or <see langword="null" /> when it is available.
    ///     <para>
    ///         The license is checked before the override, so an installation without the entitlement is told to
    ///         obtain one rather than to look for a setting it cannot have made.
    ///     </para>
    /// </summary>
    private static PremiumCapabilityUnavailableReason? ResolveUnavailableReason(
        PremiumCapabilityDefinition definition,
        LicenseState licenseState,
        bool isDisabledByOverride)
    {
        if (definition.RequiresCommercial)
        {
            if (licenseState.Edition != InstallationEdition.Commercial)
            {
                return UnlicensedReasonFor(licenseState.Stage);
            }

            if (!NamesCapability(licenseState, definition.Key))
            {
                return PremiumCapabilityUnavailableReason.NotInLicense;
            }
        }

        return isDisabledByOverride ? PremiumCapabilityUnavailableReason.DisabledByOverride : null;
    }

    /// <summary>
    ///     Returns why an installation that is not on the commercial edition has no entitlement to give. A
    ///     license that ran out and one whose term has not started are told apart from having none at all,
    ///     because renewing, waiting and obtaining a license are three different things for an operator to do.
    /// </summary>
    private static PremiumCapabilityUnavailableReason UnlicensedReasonFor(LicenseStage stage)
    {
        return stage switch
        {
            LicenseStage.Reverted => PremiumCapabilityUnavailableReason.Reverted,
            LicenseStage.NotYetValid => PremiumCapabilityUnavailableReason.NotYetValid,
            _ => PremiumCapabilityUnavailableReason.NoLicense,
        };
    }

    /// <summary>
    ///     Whether the license names the capability key. The comparison ignores case, matching how the catalog
    ///     looks a key up, so a license that differs only in casing still grants what it lists.
    /// </summary>
    private static bool NamesCapability(LicenseState licenseState, string capabilityKey)
    {
        return licenseState.Claims?.Capabilities.Contains(capabilityKey, StringComparer.OrdinalIgnoreCase) == true;
    }

    private static PremiumCapabilityDto ToDto(CapabilitySnapshot snapshot)
    {
        return new PremiumCapabilityDto(
            snapshot.Key,
            snapshot.DisplayName,
            snapshot.RequiresCommercial,
            snapshot.OverrideState,
            snapshot.IsAvailable,
            snapshot.Message,
            snapshot.Reason);
    }
}
