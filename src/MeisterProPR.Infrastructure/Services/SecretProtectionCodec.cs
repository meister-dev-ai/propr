// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.DataProtection;

namespace MeisterProPR.Infrastructure.Services;

/// <summary>Wraps ASP.NET Core Data Protection for secret values stored in the database.</summary>
public sealed class SecretProtectionCodec(IDataProtectionProvider dataProtectionProvider) : ISecretProtectionCodec
{
    private const string ProtectedPrefix = "mpr-protected:v1:";

    /// <inheritdoc />
    public string Protect(string plaintext, string purpose)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return plaintext;
        }

        var protector = dataProtectionProvider.CreateProtector(purpose);
        return ProtectedPrefix + protector.Protect(plaintext);
    }

    /// <inheritdoc />
    public string Unprotect(string value, string purpose)
    {
        if (!this.IsProtected(value))
        {
            return value;
        }

        var protector = dataProtectionProvider.CreateProtector(purpose);
        return protector.Unprotect(value[ProtectedPrefix.Length..]);
    }

    /// <inheritdoc />
    public bool IsProtected(string value)
    {
        return !string.IsNullOrEmpty(value) && value.StartsWith(ProtectedPrefix, StringComparison.Ordinal);
    }
}
