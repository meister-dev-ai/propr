// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;

internal sealed class GitHubWebhookSignatureVerifier
{
    public bool Verify(string payload, string secret, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(secret) ||
            string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        const string prefix = "sha256=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var providedSignature = signatureHeader[prefix.Length..].Trim();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expectedBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var expectedSignature = Convert.ToHexString(expectedBytes).ToLowerInvariant();

        var providedBytes = Encoding.ASCII.GetBytes(providedSignature);
        var expectedSignatureBytes = Encoding.ASCII.GetBytes(expectedSignature);
        return providedBytes.Length == expectedSignatureBytes.Length &&
               CryptographicOperations.FixedTimeEquals(providedBytes, expectedSignatureBytes);
    }
}
