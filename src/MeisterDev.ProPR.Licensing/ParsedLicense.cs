// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MeisterDev.ProPR.Licensing;

/// <summary>
///     A license document that parsed: its claims, the certificates its header carried, and the bytes the
///     signature covers.
///     <para>
///         Whether the signature was checked depends on which reader call produced this instance. Either
///         overload of <see cref="LicenseReader.Read(string, ECDsa)" /> checks it before reading any claim.
///         <see cref="LicenseReader.Parse" /> does not, so a caller that parses first treats the claims as
///         unverified until <see cref="HasValidSignature" /> holds.
///     </para>
///     <para>The instance owns the loaded certificates. Dispose it when the chain is no longer needed.</para>
/// </summary>
public sealed class ParsedLicense : IDisposable
{
    private readonly X509Certificate2[] _signingChain;
    private readonly byte[] _signedBytes;
    private readonly byte[] _signature;

    internal ParsedLicense(LicenseClaims claims, X509Certificate2[] signingChain, byte[] signedBytes, byte[] signature)
    {
        this.Claims = claims;
        this._signingChain = signingChain;
        this._signedBytes = signedBytes;
        this._signature = signature;
    }

    /// <summary>The payload the document carries.</summary>
    public LicenseClaims Claims { get; }

    /// <summary>
    ///     The certificates from the header's <c>x5c</c> parameter, signing certificate first. A trailing
    ///     root, when the issuer included one, is carried through unchanged; building a chain against a trust
    ///     anchor is the caller's job.
    /// </summary>
    public IReadOnlyList<X509Certificate2> SigningChain => this._signingChain;

    /// <summary>The first certificate in <see cref="SigningChain" />, which is the one that signed.</summary>
    public X509Certificate2 SigningCertificate => this._signingChain[0];

    /// <summary>
    ///     Creates the public key of <see cref="SigningCertificate" />. Reading the document established that
    ///     the certificate carries an ECDSA P-256 key, so this returns a key rather than null. The caller owns
    ///     the returned key and disposes it.
    /// </summary>
    /// <returns>The signer's public key.</returns>
    public ECDsa CreateSignerPublicKey() => this.SigningCertificate.GetECDsaPublicKey()!;

    /// <summary>
    ///     Checks the signature over the header and payload against a public key.
    /// </summary>
    /// <param name="signerPublicKey">The key to check against.</param>
    /// <returns>Whether the signature holds.</returns>
    public bool HasValidSignature(ECDsa signerPublicKey)
    {
        ArgumentNullException.ThrowIfNull(signerPublicKey);

        try
        {
            return signerPublicKey.VerifyData(
                this._signedBytes,
                this._signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            // A key of the wrong curve or size cannot confirm the document. That outcome is a refusal, not a
            // fault of the caller, so it is reported the same way a signature that does not hold is.
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var certificate in this._signingChain)
        {
            certificate.Dispose();
        }
    }
}
