// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MeisterDev.ProPR.Licensing;

/// <summary>
///     The root certificate a license's signing chain has to lead back to.
///     <para>
///         The anchor a build trusts is the certificate committed next to this file and compiled into the
///         assembly as an embedded resource. No build property, environment variable, mounted file,
///         configuration key, or database value supplies or replaces it, so which signers an installation accepts
///         follows from the source it was built from and from nothing an installation can set.
///     </para>
///     <para>
///         A verifier takes an anchor as a constructor input rather than reading the resource itself, which
///         leaves one path to the shipped anchor and lets a test verify against a chain of its own.
///         <see cref="None" /> is the state of a build that carries no anchor; a verifier handed it refuses with
///         <see cref="LicenseFailureReason.NoAnchorInThisBuild" />.
///     </para>
///     <para>The instance owns the certificate it holds. Dispose it when the anchor is no longer needed.</para>
/// </summary>
public sealed class LicenseTrustAnchor : IDisposable
{
    // The build names a resource embedded from the project root after the root namespace and the file name.
    private const string ResourceName = "MeisterDev.ProPR.Licensing.licensing-root.cer";

    private readonly X509Certificate2? _certificate;

    private LicenseTrustAnchor(X509Certificate2? certificate)
    {
        this._certificate = certificate;
    }

    /// <summary>Whether this anchor carries a certificate to build chains against.</summary>
    [MemberNotNullWhen(true, nameof(Certificate))]
    public bool IsPresent => this._certificate is not null;

    /// <summary>The root certificate, or <see langword="null" /> when no anchor is present.</summary>
    public X509Certificate2? Certificate => this._certificate;

    /// <summary>
    ///     The anchor compiled into this build.
    ///     <para>
    ///         A resource that is missing, empty, or not a certificate yields the absent anchor rather than an
    ///         exception. A build without a usable anchor verifies no license, which the verifier reports as its
    ///         own refusal reason; raising here would instead fault whichever call site happened to read the
    ///         anchor first.
    ///     </para>
    /// </summary>
    /// <returns>The anchor, present or absent.</returns>
    public static LicenseTrustAnchor FromThisBuild() => new(TryLoadEmbeddedCertificate());

    /// <summary>The state of a build that ships no anchor.</summary>
    /// <returns>An absent anchor.</returns>
    public static LicenseTrustAnchor None() => new(null);

    /// <summary>
    ///     An anchor over a caller-supplied root, which is how a test verifies against a chain it generated.
    /// </summary>
    /// <param name="rootCertificate">The root certificate. The anchor takes ownership and disposes it.</param>
    /// <returns>The anchor.</returns>
    public static LicenseTrustAnchor Of(X509Certificate2 rootCertificate)
    {
        ArgumentNullException.ThrowIfNull(rootCertificate);

        return new LicenseTrustAnchor(rootCertificate);
    }

    /// <inheritdoc />
    public void Dispose() => this._certificate?.Dispose();

    private static X509Certificate2? TryLoadEmbeddedCertificate()
    {
        using var resource = typeof(LicenseTrustAnchor).Assembly.GetManifestResourceStream(ResourceName);

        if (resource is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var der = buffer.ToArray();

        if (der.Length == 0)
        {
            return null;
        }

        try
        {
            return X509CertificateLoader.LoadCertificate(der);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
