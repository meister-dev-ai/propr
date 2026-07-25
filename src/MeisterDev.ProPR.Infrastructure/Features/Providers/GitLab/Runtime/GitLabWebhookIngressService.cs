// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Parsing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Runtime;

internal sealed class GitLabWebhookIngressService(
    IClientScmConnectionRepository connectionRepository,
    GitLabWebhookTokenVerifier tokenVerifier,
    GitLabWebhookPayloadParser payloadParser,
    IClientRegistry clientRegistry) : IWebhookIngressService
{
    public ScmProvider Provider => ScmProvider.GitLab;

    public async Task<bool> VerifyAsync(
        Guid clientId,
        ProviderHostRef host,
        IReadOnlyDictionary<string, string> headers,
        string payload,
        string? verificationSecret = null,
        CancellationToken ct = default)
    {
        EnsureGitLab(host);

        if (string.IsNullOrWhiteSpace(verificationSecret))
        {
            return false;
        }

        var connection = await connectionRepository.GetOperationalConnectionAsync(clientId, host, ct);
        if (connection is null)
        {
            return false;
        }

        return tokenVerifier.Verify(verificationSecret, TryReadHeader(headers, "X-Gitlab-Token"));
    }

    public async Task<WebhookDeliveryEnvelope> ParseAsync(
        Guid clientId,
        ProviderHostRef host,
        IReadOnlyDictionary<string, string> headers,
        string payload,
        CancellationToken ct = default)
    {
        EnsureGitLab(host);

        var configuredReviewer = await clientRegistry.GetReviewerIdentityAsync(clientId, host, ct);
        return payloadParser.Parse(host, headers, payload, configuredReviewer);
    }

    private static void EnsureGitLab(ProviderHostRef host)
    {
        if (host.Provider != ScmProvider.GitLab)
        {
            throw new InvalidOperationException("This GitLab adapter only supports GitLab provider references.");
        }
    }

    private static string? TryReadHeader(IReadOnlyDictionary<string, string> headers, string headerName)
    {
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, headerName, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        return null;
    }
}
