// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Interfaces;

/// <summary>Protects and restores sensitive values for storage at rest.</summary>
public interface ISecretProtectionCodec
{
    /// <summary>Protects a plaintext value for the supplied logical purpose.</summary>
    string Protect(string plaintext, string purpose);

    /// <summary>
    ///     Restores a previously protected value for the supplied logical purpose.
    ///     Returns the input unchanged when the value is legacy plaintext.
    /// </summary>
    string Unprotect(string value, string purpose);

    /// <summary>Returns true when the supplied value matches the protected storage envelope.</summary>
    bool IsProtected(string value);
}
