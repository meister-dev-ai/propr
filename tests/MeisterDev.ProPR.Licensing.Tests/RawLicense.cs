// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MeisterDev.ProPR.Licensing.Tests;

/// <summary>
///     Builds license documents from raw header and payload JSON.
///     <para>
///         The writer refuses to produce the shapes the refusal tests need, so those tests assemble the
///         compact form themselves. Everything else here matches what the writer does, which keeps the
///         difference between a test document and a real one down to the one member under test.
///     </para>
/// </summary>
internal static class RawLicense
{
    /// <summary>The claim members of a payload the reader accepts, ready to be altered member by member.</summary>
    public static Dictionary<string, string> AcceptedClaims() => new()
    {
        ["schemaVersion"] = "1",
        ["jti"] = "\"6c2a4d1e-8f13-4c0a-9b47-2d5e6f7a8b90\"",
        ["licensee"] = "\"Raw Document Fixture\"",
        ["iat"] = "1767225600",
        ["nbf"] = "1767225600",
        ["exp"] = "2082758400",
        ["capabilities"] = "[\"review\"]",
    };

    public static string PayloadJson(IReadOnlyDictionary<string, string> claims)
        => "{" + string.Join(",", claims.Select(claim => $"\"{claim.Key}\":{claim.Value}")) + "}";

    /// <summary>A header carrying the test chain, with the algorithm and any extra parameters under test.</summary>
    public static string HeaderJson(string algorithm = LicenseDocument.Algorithm, string extraParameters = "")
    {
        using var signer = TestLicenseChain.LoadSigningCertificate();
        using var root = TestLicenseChain.LoadRootCertificate();

        return HeaderJson([signer, root], algorithm, extraParameters);
    }

    /// <summary>A header carrying the certificates the test supplies, signing certificate first.</summary>
    public static string HeaderJson(
        IReadOnlyList<X509Certificate2> chain,
        string algorithm = LicenseDocument.Algorithm,
        string extraParameters = "")
    {
        var entries = string.Join(",", chain.Select(certificate => $"\"{Convert.ToBase64String(certificate.RawData)}\""));

        return $"{{\"alg\":\"{algorithm}\",\"typ\":\"{LicenseDocument.Type}\",\"x5c\":[{entries}]{extraParameters}}}";
    }

    /// <summary>A header with the <c>typ</c> parameter the test supplies, or none when it supplies null.</summary>
    public static string HeaderJsonWithType(string? typeJson)
    {
        using var signer = TestLicenseChain.LoadSigningCertificate();
        using var root = TestLicenseChain.LoadRootCertificate();
        var entries = $"\"{Convert.ToBase64String(signer.RawData)}\",\"{Convert.ToBase64String(root.RawData)}\"";
        var typeParameter = typeJson is null ? string.Empty : $",\"typ\":{typeJson}";

        return $"{{\"alg\":\"{LicenseDocument.Algorithm}\"{typeParameter},\"x5c\":[{entries}]}}";
    }

    /// <summary>Signs raw header and payload JSON with the test signing key.</summary>
    public static string Sign(string headerJson, string payloadJson)
    {
        using var key = TestLicenseChain.LoadSigningKey();

        return SignWith(key, headerJson, payloadJson);
    }

    /// <summary>
    ///     Signs raw header and payload JSON with a key the caller supplies, which is how a test produces a
    ///     document whose signature and whose header certificates come from different keys.
    /// </summary>
    public static string SignWith(ECDsa signingKey, string headerJson, string payloadJson)
    {
        var signedText = string.Concat(Encode(headerJson), ".", Encode(payloadJson));
        var signature = signingKey.SignData(
            Encoding.ASCII.GetBytes(signedText),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return string.Concat(signedText, ".", Base64Url.EncodeToString(signature));
    }

    /// <summary>Signs the accepted payload with one claim replaced or added.</summary>
    public static string SignWithClaim(string name, string json)
    {
        var claims = AcceptedClaims();
        claims[name] = json;

        return Sign(HeaderJson(), PayloadJson(claims));
    }

    /// <summary>Replaces the payload segment of a signed document, leaving the original signature in place.</summary>
    public static string ReplacePayload(string compactLicense, string payloadJson)
    {
        var segments = compactLicense.Split('.');

        return string.Join('.', segments[0], Encode(payloadJson), segments[2]);
    }

    public static string Encode(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
