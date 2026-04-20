// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;

internal sealed class ForgejoWebhookSignatureVerifier
{
    public bool Verify(string payload, string secret, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(secret) ||
            string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        var providedSignature = signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? signatureHeader[7..].Trim()
            : signatureHeader.Trim();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expectedSignature =
            Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var providedBytes = Encoding.ASCII.GetBytes(providedSignature);
        var expectedBytes = Encoding.ASCII.GetBytes(expectedSignature);
        return providedBytes.Length == expectedBytes.Length
               && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
