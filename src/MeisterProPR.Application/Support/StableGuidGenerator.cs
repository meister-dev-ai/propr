// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;

namespace MeisterProPR.Application.Support;

/// <summary>Creates deterministic GUIDs from stable string identifiers.</summary>
public static class StableGuidGenerator
{
    /// <summary>Creates a deterministic GUID from the supplied identifier text.</summary>
    public static Guid Create(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(value.Trim()));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, bytes.Length);
        return new Guid(bytes);
    }
}
