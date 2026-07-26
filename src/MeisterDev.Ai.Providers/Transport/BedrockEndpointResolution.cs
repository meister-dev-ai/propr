// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics.CodeAnalysis;
using Amazon.Runtime;
using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     Turns a stored Bedrock profile into the two things the AWS SDK needs: a region and a credential.
/// </summary>
/// <remarks>
///     <para>
///         The region is read from the endpoint URL — <c>bedrock-runtime.eu-central-1.amazonaws.com</c> names
///         <c>eu-central-1</c> — rather than kept as a separate setting. Residency is the reason: the URL an
///         operator looks at is then the whole answer to "where does this traffic go", and there is no second
///         field that can disagree with it. A private or VPC endpoint that does not name a region in its host may
///         supply one as a <c>region</c> query parameter instead.
///     </para>
///     <para>
///         Credentials are explicit rather than taken from the host's own AWS credential chain. In a
///         multi-tenant control plane the ambient identity belongs to the operator, not to the tenant whose
///         review is running, so falling back to it would bill and authorize one tenant's traffic against
///         another's role. Serving an ambient IAM role properly needs a tenant-scoped role assumption, which is
///         its own piece of work.
///     </para>
/// </remarks>
public static class BedrockEndpointResolution
{
    /// <summary>The query parameter naming the region when the endpoint host does not.</summary>
    public const string RegionParameterName = "region";

    /// <summary>
    ///     Reads the region for an endpoint, or <see langword="null" /> when neither the host nor the query
    ///     parameters name one.
    /// </summary>
    /// <param name="endpoint">The stored provider endpoint.</param>
    public static string? ResolveRegion(ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint.DefaultQueryParams is { } parameters
            && parameters.TryGetValue(RegionParameterName, out var configured)
            && !string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var uri)
            ? RegionFromHost(uri.Host)
            : null;
    }

    /// <summary>
    ///     Reads the region named by an AWS service host, or <see langword="null" /> when the host does not name
    ///     one. AWS service hosts are <c>&lt;service&gt;.&lt;region&gt;.amazonaws.com</c>.
    /// </summary>
    /// <param name="host">The host to read.</param>
    public static string? RegionFromHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < labels.Length - 1; index++)
        {
            if (LooksLikeRegion(labels[index]) && labels[index + 1].Equals("amazonaws", StringComparison.OrdinalIgnoreCase))
            {
                return labels[index].ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>
    ///     Builds the credential from the stored secret, which holds an access-key pair as
    ///     <c>accessKeyId:secretAccessKey</c> and optionally a session token as a third part.
    /// </summary>
    /// <param name="endpoint">The stored provider endpoint.</param>
    /// <exception cref="InvalidOperationException">The secret is missing or is not an access-key pair.</exception>
    public static AWSCredentials ResolveCredentials(ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!TryReadKeyPair(endpoint.Secret, out var accessKeyId, out var secretAccessKey, out var sessionToken))
        {
            throw new InvalidOperationException(
                "An AWS Bedrock connection needs an access key. Store it as 'accessKeyId:secretAccessKey', "
                + "adding ':sessionToken' for temporary credentials.");
        }

        return sessionToken is null
            ? new BasicAWSCredentials(accessKeyId, secretAccessKey)
            : new SessionAWSCredentials(accessKeyId, secretAccessKey, sessionToken);
    }

    /// <summary>Reports whether a stored secret carries a usable access-key pair.</summary>
    /// <param name="secret">The stored secret.</param>
    public static bool LooksLikeKeyPair(string? secret)
    {
        return TryReadKeyPair(secret, out _, out _, out _);
    }

    private static bool TryReadKeyPair(
        string? secret,
        [NotNullWhen(true)] out string? accessKeyId,
        [NotNullWhen(true)] out string? secretAccessKey,
        out string? sessionToken)
    {
        accessKeyId = null;
        secretAccessKey = null;
        sessionToken = null;

        if (string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        var parts = secret.Split(':', 3, StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return false;
        }

        accessKeyId = parts[0];
        secretAccessKey = parts[1];
        sessionToken = parts.Length == 3 && parts[2].Length > 0 ? parts[2] : null;
        return true;
    }

    // An AWS region label: two or more letters, a dash, a word, a dash, a digit — us-east-1, eu-central-1,
    // ap-southeast-2, us-gov-west-1. Matching the shape rather than a list keeps new regions working.
    private static bool LooksLikeRegion(string label)
    {
        var parts = label.Split('-');
        return parts.Length >= 3
               && parts[0].All(char.IsAsciiLetter)
               && parts[0].Length >= 2
               && parts[^1].All(char.IsAsciiDigit)
               && parts[^1].Length >= 1;
    }
}
