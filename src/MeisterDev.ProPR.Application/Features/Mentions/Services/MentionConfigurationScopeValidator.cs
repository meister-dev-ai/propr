// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Mentions.Services;

/// <summary>Validates mention coverage against saved native scopes or enabled provider connections.</summary>
public sealed class MentionConfigurationScopeValidator(
    IClientScmConnectionRepository connectionRepository,
    IScmProviderRegistry providerRegistry,
    IClientScmScopeRepository? scopeRepository = null)
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

        var selection = providerRegistry.GetSourceIdentityPolicy(provider).PrepareMentionScopeSelection(scopePath);
        var isKnown = selection.ScopeType is not null
            ? await this.IsConfiguredScopeAsync(clientId, provider, selection, ct)
            : await this.IsConnectionHostAsync(clientId, provider, selection, ct);

        return isKnown
            ? MentionScopeVerdict.Accepted
            : new MentionScopeVerdict(MentionScopeRefusal.UnknownScopePath, UnknownScopePathMessage);
    }

    private async Task<bool> IsConfiguredScopeAsync(Guid clientId, ScmProvider provider, ReviewMentionScopeSelection selection, CancellationToken ct)
    {
        if (scopeRepository is null)
        {
            return false;
        }

        var scopes = await scopeRepository.GetByClientIdAsync(clientId, provider, selection.ScopeType!, ct);
        return scopes.Any(scope =>
            scope.IsEnabled
            && selection.MatchesScope(scope.ScopePath));
    }

    private async Task<bool> IsConnectionHostAsync(
        Guid clientId,
        ScmProvider provider,
        ReviewMentionScopeSelection selection,
        CancellationToken ct)
    {
        var connections = await connectionRepository.GetByClientIdAsync(clientId, ct);
        return connections.Any(connection =>
            connection.ProviderFamily == provider
            && connection.IsActive
            && selection.MatchesScope(connection.HostBaseUrl));
    }
}
