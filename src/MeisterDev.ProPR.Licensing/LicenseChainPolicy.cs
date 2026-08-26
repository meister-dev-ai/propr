// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MeisterDev.ProPR.Licensing;

/// <summary>
///     Builds the certificate path from a license's signing certificate to the trust anchor a build carries.
///     <para>
///         The policy is stated here in one place, and building a chain goes through it, so the settings that
///         decide what a chain means cannot differ between call sites.
///     </para>
/// </summary>
internal static class LicenseChainPolicy
{
    /// <summary>
    ///     Whether the signing certificate is an end-entity certificate that leads to the anchor under this
    ///     policy.
    /// </summary>
    /// <param name="signingChain">The certificates from the document's header, signing certificate first.</param>
    /// <param name="anchor">The only certificate treated as a root.</param>
    /// <param name="verificationTime">The instant certificate validity is evaluated at.</param>
    /// <param name="failureDetail">Why the signing certificate is not accepted, when it is not.</param>
    /// <returns>Whether the path holds.</returns>
    internal static bool LeadsToAnchor(
        IReadOnlyList<X509Certificate2> signingChain,
        X509Certificate2 anchor,
        DateTimeOffset verificationTime,
        out string failureDetail)
    {
        // The signing certificate the issuing authority produces carries basic constraints marking it an end
        // entity, so a document presenting a certificate authority as its signer did not come from that
        // issuance path. Without this check an intermediate, or the anchor itself, would be accepted as a
        // signer on the strength of leading to the anchor.
        if (IsCertificateAuthority(signingChain[0]))
        {
            failureDetail = "The signing certificate is not an end-entity certificate: its basic constraints mark it a certificate authority.";

            return false;
        }

        X509Chain? chain = null;

        try
        {
            // Creating the chain reads the anchor and loads a copy of it, so it can raise for the same reasons
            // building the path can and is guarded alongside it.
            chain = CreateChain(signingChain, anchor, verificationTime);

            if (!chain.Build(signingChain[0]))
            {
                failureDetail = $"The signing certificate does not lead to the trust anchor this build carries: {DescribeStatus(chain)}.";

                return false;
            }

            if (!TerminatesAt(chain, anchor))
            {
                failureDetail = "The certificate path terminates at a certificate other than the trust anchor this build carries.";

                return false;
            }

            failureDetail = string.Empty;

            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            // The certificates and the verification instant both come from the document. A chain engine that
            // cannot process either raises rather than returning a verdict, and on this path that has to read
            // as a document this build does not accept, not as a fault reaching the caller. The instant is
            // bounded when the document is read, so this is the second line rather than the first.
            failureDetail = "The certificates in the license could not be processed into a certificate path.";

            return false;
        }
        finally
        {
            if (chain is not null)
            {
                DisposeChain(chain);
            }
        }
    }

    /// <summary>
    ///     Whether the chain's terminal certificate is the anchor.
    ///     <para>
    ///         A chain built under <see cref="X509ChainTrustMode.CustomRootTrust" /> that reports success has
    ///         already terminated in the custom trust store, so this repeats a condition the chain engine
    ///         enforces. It is kept as insurance against a platform engine that treats the custom store
    ///         differently, and exposed on its own so the condition can be exercised rather than only inferred
    ///         from chains that pass anyway.
    ///     </para>
    /// </summary>
    /// <param name="chain">A chain that has been built.</param>
    /// <param name="anchor">The expected terminal certificate.</param>
    /// <returns>Whether the chain ends at the anchor.</returns>
    internal static bool TerminatesAt(X509Chain chain, X509Certificate2 anchor)
    {
        if (chain.ChainElements.Count == 0)
        {
            return false;
        }

        var terminal = chain.ChainElements[^1].Certificate;

        return CryptographicOperations.FixedTimeEquals(terminal.RawData, anchor.RawData);
    }

    /// <summary>
    ///     The configured chain. Exposed to the test project so the settings can be read back, because a wrong
    ///     one changes what verification means without changing any result the tests would otherwise see.
    /// </summary>
    /// <param name="signingChain">The certificates from the document's header, signing certificate first.</param>
    /// <param name="anchor">The only certificate treated as a root.</param>
    /// <param name="verificationTime">The instant certificate validity is evaluated at.</param>
    /// <returns>The chain, released through <see cref="DisposeChain" />.</returns>
    internal static X509Chain CreateChain(
        IReadOnlyList<X509Certificate2> signingChain,
        X509Certificate2 anchor,
        DateTimeOffset verificationTime)
    {
        var chain = new X509Chain();

        try
        {
            var policy = chain.ChainPolicy;

            // The machine's root store says nothing about who may issue a license. The one root a path may
            // terminate at is the anchor compiled into this build.
            //
            // The store holds a copy rather than the caller's certificate because releasing a chain disposes
            // everything in its trust store, and one anchor serves every verification an installation performs.
            // Adding the caller's instance would end that anchor after the first verification. The certificates
            // from the header need no copy: they go to the extra store, which is not released with the chain,
            // and each document brings its own.
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            policy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificate(anchor.RawData));

            // The certificates after the signer are material to build a path from, not certificates to trust. A
            // header may repeat the root among them; that copy is used as a path element and still has to match
            // the anchor to be trusted.
            for (var index = 1; index < signingChain.Count; index++)
            {
                policy.ExtraStore.Add(signingChain[index]);
            }

            // Revocation is a network lookup, and a license has to verify on an installation with no outbound
            // access. Issuance is controlled instead by the term the license itself carries.
            policy.RevocationMode = X509RevocationMode.NoCheck;

            // Without this, the chain engine downloads issuer certificates from the URLs a certificate names.
            // Verification is meant to complete offline, so the download path is closed rather than relied on to
            // stay unused.
            policy.DisableCertificateDownloads = true;

            // Certificate validity is evaluated at the instant the license was issued rather than at the current
            // time. A signing certificate is valid for a shorter period than the licenses it signs, so evaluating
            // it at the current time would end every license the moment its signing certificate expired.
            policy.VerificationTime = verificationTime.UtcDateTime;
            policy.VerificationTimeIgnored = false;

            return chain;
        }
        catch
        {
            // Reading the anchor and loading the copy can raise, which would leave the chain and any copy
            // already in its trust store to be released by a finalizer.
            DisposeChain(chain);

            throw;
        }
    }

    /// <summary>
    ///     Releases a chain from <see cref="CreateChain" /> together with the anchor copy in its trust store,
    ///     which the chain does not own.
    /// </summary>
    /// <param name="chain">The chain to release.</param>
    internal static void DisposeChain(X509Chain chain)
    {
        foreach (var trusted in chain.ChainPolicy.CustomTrustStore)
        {
            trusted.Dispose();
        }

        chain.Dispose();
    }

    private static bool IsCertificateAuthority(X509Certificate2 certificate)
    {
        // A certificate that states no basic constraints is not a certificate authority, so an absent extension
        // leaves the certificate accepted as an end entity.
        foreach (var extension in certificate.Extensions)
        {
            if (extension is X509BasicConstraintsExtension { CertificateAuthority: true })
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeStatus(X509Chain chain)
    {
        // The status flags are reported and the platform's own message text is not, so the diagnostic reads the
        // same on every host.
        var flags = chain.ChainStatus
            .Select(status => status.Status.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return flags.Length == 0 ? "the chain engine reported no status" : string.Join(", ", flags);
    }
}
