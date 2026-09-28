// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.DataProtection;

/// <summary>
///     Encrypts the key ring with a PKCS#12 certificate the operator holds, and reads a ring an earlier
///     certificate encrypted while that certificate is still listed.
/// </summary>
/// <remarks>
///     The key files and the certificate are then two separate things to hold, so a copy of the key directory
///     on its own reads nothing. A restore needs both.
/// </remarks>
public sealed class CertificateKeyRingProtector : IKeyRingProtector
{
    /// <summary>The name an operator selects this protector by.</summary>
    public const string ProtectorName = "certificate";

    /// <summary>The variable naming the certificate file the key ring is encrypted with.</summary>
    public const string CertificatePathKey = "MEISTER_DATA_PROTECTION_CERTIFICATE_PATH";

    /// <summary>The variable holding the password of the certificate files.</summary>
    public const string CertificatePasswordKey = "MEISTER_DATA_PROTECTION_CERTIFICATE_PASSWORD";

    /// <summary>
    ///     The variable listing certificates a key ring was encrypted with earlier, separated by a semicolon
    ///     on every platform. They decrypt, and nothing is encrypted with them.
    /// </summary>
    public const string PreviousCertificatePathsKey = "MEISTER_DATA_PROTECTION_PREVIOUS_CERTIFICATE_PATHS";

    /// <inheritdoc />
    public string Name => ProtectorName;

    /// <inheritdoc />
    public void Apply(IDataProtectionBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var certificatePath = configuration[CertificatePathKey];
        if (string.IsNullOrWhiteSpace(certificatePath))
        {
            throw new InvalidOperationException(
                $"The '{ProtectorName}' key-ring protector requires {CertificatePathKey} to name a PKCS#12 "
                + "certificate file.");
        }

        var password = configuration[CertificatePasswordKey];
        var certificate = Load(certificatePath, password);

        // Every certificate that can decrypt is listed, the current one included: the ring holds keys written
        // before the rotation, and a key stays encrypted with the certificate that wrote it until it expires.
        var decrypting = new List<X509Certificate2> { certificate };
        foreach (var previousPath in SplitPaths(configuration[PreviousCertificatePathsKey]))
        {
            decrypting.Add(Load(previousPath, password));
        }

        builder.ProtectKeysWithCertificate(certificate);
        builder.UnprotectKeysWithAnyCertificate([.. decrypting]);
    }

    /// <summary>The paths in <paramref name="value" />, which separates them by a semicolon.</summary>
    /// <param name="value">The configured list, or null or blank when none was configured.</param>
    /// <remarks>
    ///     One delimiter on every platform, so the same value means the same thing wherever the host runs. A
    ///     colon carries a drive letter on Windows and separates paths on Unix, and a comma occurs in file
    ///     names, so either would split a path an operator gave whole. A semicolon can occur in a file name
    ///     too: a certificate at such a path cannot be listed here, and the restriction is stated where the
    ///     variable is documented.
    /// </remarks>
    internal static IReadOnlyList<string> SplitPaths(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static X509Certificate2 Load(string path, string? password)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"The '{ProtectorName}' key-ring protector found no certificate file at '{path}'.");
        }

        return X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }
}
