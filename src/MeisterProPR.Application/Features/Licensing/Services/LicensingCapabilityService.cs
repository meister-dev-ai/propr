// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Dtos;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;

namespace MeisterProPR.Application.Features.Licensing.Services;

/// <summary>Calculates effective premium capability availability from the installation policy and static catalog.</summary>
public sealed class LicensingCapabilityService(
    IPremiumCapabilityCatalog capabilityCatalog,
    ILicensingPolicyStore policyStore) : ILicensingCapabilityService
{
    /// <summary>Gets a summary of the current licensing configuration and all premium capabilities.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A licensing summary containing the edition, activation date, and capability snapshots.</returns>
    public async Task<LicensingSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var policy = await policyStore.GetAsync(cancellationToken);
        var capabilities = capabilityCatalog.GetAll()
            .Select(definition => this.ResolveSnapshot(definition, policy))
            .Select(ToDto)
            .ToList()
            .AsReadOnly();

        return new LicensingSummaryDto(policy.Edition, policy.ActivatedAt, capabilities);
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
        var policy = await policyStore.GetAsync(cancellationToken);
        return this.ResolveSnapshot(definition, policy);
    }

    /// <summary>Determines whether a specific premium capability is enabled.</summary>
    /// <param name="capabilityKey">The unique key of the premium capability.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the capability is available; otherwise, false.</returns>
    public async ValueTask<bool> IsEnabledAsync(string capabilityKey, CancellationToken cancellationToken = default)
    {
        return (await this.GetCapabilityAsync(capabilityKey, cancellationToken)).IsAvailable;
    }

    /// <summary>Updates the licensing policy and premium capability overrides.</summary>
    /// <param name="edition">The installation edition to set.</param>
    /// <param name="capabilityOverrides">A collection of capability override mutations to apply.</param>
    /// <param name="actorUserId">The ID of the user performing the update, if any.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A licensing summary with the updated configuration and capability snapshots.</returns>
    /// <exception cref="InvalidOperationException">Thrown when attempting to enable a commercial-only capability in Community edition.</exception>
    public async Task<LicensingSummaryDto> UpdateAsync(
        InstallationEdition edition,
        IReadOnlyCollection<CapabilityOverrideMutation> capabilityOverrides,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilityOverrides);

        var overrideList = capabilityOverrides.ToList();
        foreach (var overrideMutation in overrideList)
        {
            var definition = capabilityCatalog.Get(overrideMutation.Key)
                             ?? throw new KeyNotFoundException($"Unknown premium capability '{overrideMutation.Key}'.");

            if (edition == InstallationEdition.Community
                && definition.RequiresCommercial
                && overrideMutation.OverrideState == PremiumCapabilityOverrideState.Enabled)
            {
                throw new InvalidOperationException($"Capability '{overrideMutation.Key}' cannot be enabled while the installation is in Community edition.");
            }
        }

        var updatedPolicy = await policyStore.UpdateAsync(edition, overrideList, actorUserId, cancellationToken);
        var capabilities = capabilityCatalog.GetAll()
            .Select(definition => this.ResolveSnapshot(definition, updatedPolicy))
            .Select(ToDto)
            .ToList()
            .AsReadOnly();

        return new LicensingSummaryDto(updatedPolicy.Edition, updatedPolicy.ActivatedAt, capabilities);
    }

    private CapabilitySnapshot ResolveSnapshot(
        PremiumCapabilityDefinition definition,
        InstallationLicensingPolicy policy)
    {
        var overrideState = policy.GetOverrideState(definition.Key);
        var isCommercial = policy.Edition == InstallationEdition.Commercial;

        bool isAvailable;
        if (!definition.RequiresCommercial)
        {
            isAvailable = overrideState switch
            {
                PremiumCapabilityOverrideState.Disabled => false,
                PremiumCapabilityOverrideState.Enabled => true,
                _ => true,
            };
        }
        else if (!isCommercial)
        {
            isAvailable = false;
        }
        else
        {
            isAvailable = overrideState switch
            {
                PremiumCapabilityOverrideState.Enabled => true,
                PremiumCapabilityOverrideState.Disabled => false,
                _ => definition.DefaultWhenCommercial,
            };
        }

        var message = isAvailable
            ? null
            : !isCommercial && definition.RequiresCommercial
                ? definition.CommercialRequiredMessage
                : definition.CommercialDisabledMessage;

        return new CapabilitySnapshot(
            definition.Key,
            definition.DisplayName,
            definition.RequiresCommercial,
            definition.DefaultWhenCommercial,
            overrideState,
            isAvailable,
            message);
    }

    private static PremiumCapabilityDto ToDto(CapabilitySnapshot snapshot)
    {
        return new PremiumCapabilityDto(
            snapshot.Key,
            snapshot.DisplayName,
            snapshot.RequiresCommercial,
            snapshot.DefaultWhenCommercial,
            snapshot.OverrideState,
            snapshot.IsAvailable,
            snapshot.Message);
    }
}
