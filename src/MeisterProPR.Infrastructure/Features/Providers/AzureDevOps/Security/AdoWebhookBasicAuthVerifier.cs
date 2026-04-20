// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Security;

/// <summary>Verifies Azure DevOps Basic-auth headers against protected webhook secrets.</summary>
public sealed class AdoWebhookBasicAuthVerifier(ISecretProtectionCodec secretProtectionCodec)
    : IAdoWebhookBasicAuthVerifier
{
    /// <inheritdoc />
    public bool IsAuthorized(string? authorizationHeader, string protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var encodedCredentials = authorizationHeader[6..].Trim();
            var decodedCredentials = Encoding.UTF8.GetString(Convert.FromBase64String(encodedCredentials));
            var separatorIndex = decodedCredentials.IndexOf(':');
            if (separatorIndex < 0)
            {
                return false;
            }

            var suppliedSecret = decodedCredentials[(separatorIndex + 1)..];
            var storedSecret = secretProtectionCodec.Unprotect(protectedSecret, "WebhookSecret");

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(suppliedSecret),
                Encoding.UTF8.GetBytes(storedSecret));
        }
        catch
        {
            return false;
        }
    }
}
