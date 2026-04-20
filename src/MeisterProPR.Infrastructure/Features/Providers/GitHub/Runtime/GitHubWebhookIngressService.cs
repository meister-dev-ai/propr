// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Parsing;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Runtime;

internal sealed class GitHubWebhookIngressService(
    IClientScmConnectionRepository connectionRepository,
    GitHubWebhookSignatureVerifier signatureVerifier,
    GitHubWebhookPayloadParser payloadParser,
    IClientRegistry clientRegistry) : IWebhookIngressService
{
    public ScmProvider Provider => ScmProvider.GitHub;

    public async Task<bool> VerifyAsync(
        Guid clientId,
        ProviderHostRef host,
        IReadOnlyDictionary<string, string> headers,
        string payload,
        string? verificationSecret = null,
        CancellationToken ct = default)
    {
        EnsureGitHub(host);

        if (string.IsNullOrWhiteSpace(verificationSecret))
        {
            return false;
        }

        var connection = await connectionRepository.GetOperationalConnectionAsync(clientId, host, ct);
        if (connection is null)
        {
            return false;
        }

        return signatureVerifier.Verify(payload, verificationSecret, TryReadHeader(headers, "X-Hub-Signature-256"));
    }

    public async Task<WebhookDeliveryEnvelope> ParseAsync(
        Guid clientId,
        ProviderHostRef host,
        IReadOnlyDictionary<string, string> headers,
        string payload,
        CancellationToken ct = default)
    {
        EnsureGitHub(host);

        var configuredReviewer = await clientRegistry.GetReviewerIdentityAsync(clientId, host, ct);
        return payloadParser.Parse(host, headers, payload, configuredReviewer);
    }

    private static void EnsureGitHub(ProviderHostRef host)
    {
        if (host.Provider != ScmProvider.GitHub)
        {
            throw new InvalidOperationException("This GitHub adapter only supports GitHub provider references.");
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
