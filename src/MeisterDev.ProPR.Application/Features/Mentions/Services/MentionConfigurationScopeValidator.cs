// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Mentions.Services;

/// <summary>
///     Holds a mention configuration to the connections its client has already set up.
/// </summary>
/// <remarks>
///     A stored scope path reaches a provider client at scan time, which resolves credentials by looking up a
///     connection for that host. An unknown host resolves to no credential, and the runtime answers an absent
///     credential by acquiring a token from the platform's own managed identity and presenting it to the host
///     in the URL. Without this rule a client administrator can therefore have the platform identity offered to
///     a host of their choosing.
///     The rule is one rule for every provider. The previous check ran only for Azure DevOps, so naming any
///     other provider skipped it and stored an arbitrary URL.
/// </remarks>
public sealed class MentionConfigurationScopeValidator(
    IClientScmConnectionRepository connectionRepository,
    IScmProviderRegistry providerRegistry,
    IClientAdoOrganizationScopeRepository? organizationScopeRepository = null)
    : IMentionConfigurationScopeValidator
{
    private const string UnsupportedProviderMessage =
        "This installation cannot discover pull requests for that provider, so a mention configuration for it would never be scanned.";

    private const string UnknownScopePathMessage =
        "That scope path does not match an enabled connection this client holds for the selected provider. Add and enable the connection first.";

    private const string CannotPublishRepliesMessage =
        "That provider offers no way to reply inside a review conversation, so a question asked there could not be answered where it was asked.";

    /// <inheritdoc />
    public async Task<MentionScopeVerdict> ValidateAsync(
        Guid clientId,
        ScmProvider provider,
        string scopePath,
        CancellationToken ct = default)
    {
        if (!providerRegistry.SupportsActivePullRequestDiscovery(provider))
        {
            return new MentionScopeVerdict(MentionScopeRefusal.UnsupportedProvider, UnsupportedProviderMessage);
        }

        // Answering requires both discovery and a reply publisher. A provider with only discovery would
        // accept a configuration and scan on it while never posting an answer.
        if (!providerRegistry.SupportsReviewThreadReply(provider))
        {
            return new MentionScopeVerdict(
                MentionScopeRefusal.CannotPublishReplies,
                CannotPublishRepliesMessage);
        }

        if (string.IsNullOrWhiteSpace(scopePath))
        {
            return new MentionScopeVerdict(MentionScopeRefusal.UnknownScopePath, UnknownScopePathMessage);
        }

        var isKnown = provider == ScmProvider.AzureDevOps
            ? await this.IsConfiguredOrganizationAsync(clientId, scopePath, ct)
            : await this.IsConnectionHostAsync(clientId, provider, scopePath, ct);

        return isKnown
            ? MentionScopeVerdict.Accepted
            : new MentionScopeVerdict(MentionScopeRefusal.UnknownScopePath, UnknownScopePathMessage);
    }

    /// <summary>
    ///     Reduces two spellings of one endpoint to the same string, so a trailing separator or a difference in
    ///     case is a match rather than a refusal.
    /// </summary>
    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('/');
    }

    /// <summary>
    ///     An Azure DevOps configuration names an organization, which sits under the connection rather than
    ///     being the connection's own host, so the organization scopes are what it is matched against.
    /// </summary>
    private async Task<bool> IsConfiguredOrganizationAsync(Guid clientId, string scopePath, CancellationToken ct)
    {
        if (organizationScopeRepository is null)
        {
            return false;
        }

        var scopes = await organizationScopeRepository.GetByClientIdAsync(clientId, ct);
        return scopes.Any(scope =>
            scope.IsEnabled
            && string.Equals(Normalize(scope.OrganizationUrl), Normalize(scopePath), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Every other provider is addressed by its host, which is the connection's own. The provider named in
    ///     the request decides which connection counts, so a client holding a GitHub and a Forgejo connection at
    ///     one host cannot name one and be scanned through the other.
    /// </summary>
    private async Task<bool> IsConnectionHostAsync(
        Guid clientId,
        ScmProvider provider,
        string scopePath,
        CancellationToken ct)
    {
        var connections = await connectionRepository.GetByClientIdAsync(clientId, ct);
        return connections.Any(connection =>
            connection.ProviderFamily == provider
            && connection.IsActive
            && string.Equals(
                Normalize(connection.HostBaseUrl),
                Normalize(scopePath),
                StringComparison.OrdinalIgnoreCase));
    }
}
