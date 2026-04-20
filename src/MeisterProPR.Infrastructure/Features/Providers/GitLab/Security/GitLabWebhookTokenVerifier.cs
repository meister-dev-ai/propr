// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;

internal sealed class GitLabWebhookTokenVerifier
{
    public bool Verify(string secret, string? tokenHeader)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(tokenHeader))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(tokenHeader.Trim());
        var expectedBytes = Encoding.UTF8.GetBytes(secret);
        return providedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
