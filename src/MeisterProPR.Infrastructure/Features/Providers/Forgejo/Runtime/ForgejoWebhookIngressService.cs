// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Parsing;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.Runtime;

internal sealed class ForgejoWebhookIngressService(
    IClientScmConnectionRepository connectionRepository,
    ForgejoWebhookSignatureVerifier signatureVerifier,
    ForgejoWebhookPayloadParser payloadParser,
    IClientRegistry clientRegistry) : IWebhookIngressService
{
    public ScmProvider Provider => ScmProvider.Forgejo;

    public async Task<bool> VerifyAsync(
        Guid clientId,
        ProviderHostRef host,
        IReadOnlyDictionary<string, string> headers,
        string payload,
        string? verificationSecret = null,
        CancellationToken ct = default)
    {
        EnsureForgejo(host);

        if (string.IsNullOrWhiteSpace(verificationSecret))
        {
            return false;
        }

        var connection = await connectionRepository.GetOperationalConnectionAsync(clientId, host, ct);
        if (connection is null)
        {
            return false;
        }

        return signatureVerifier.Verify(payload, verificationSecret, TryReadHeader(headers, "X-Gitea-Signature"));
    }

    public async Task<WebhookDeliveryEnvelope> ParseAsync(
        Guid clientId,
        ProviderHostRef host,
        IReadOnlyDictionary<string, string> headers,
        string payload,
        CancellationToken ct = default)
    {
        EnsureForgejo(host);

        var configuredReviewer = await clientRegistry.GetReviewerIdentityAsync(clientId, host, ct);
        return payloadParser.Parse(host, headers, payload, configuredReviewer);
    }

    private static void EnsureForgejo(ProviderHostRef host)
    {
        if (host.Provider != ScmProvider.Forgejo)
        {
            throw new InvalidOperationException("This Forgejo adapter only supports Forgejo provider references.");
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
