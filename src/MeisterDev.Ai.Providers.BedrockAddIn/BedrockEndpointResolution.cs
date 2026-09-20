// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.Ai.Providers.BedrockAddIn;

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

    /// <summary>The AWS public partition's service-host suffix.</summary>
    public const string AwsHostSuffix = ".amazonaws.com";

    /// <summary>The China partition's service-host suffix.</summary>
    public const string AwsChinaHostSuffix = ".amazonaws.com.cn";

    /// <summary>The service labels Bedrock answers on: the runtime surface and the control plane.</summary>
    public static readonly string[] BedrockServiceLabels = ["bedrock-runtime", "bedrock"];

    /// <summary>Reports whether a host is an AWS service host in either partition.</summary>
    /// <param name="host">The host component of the address.</param>
    public static bool IsAwsHost(string? host)
    {
        return host is not null
               && (host.EndsWith(AwsHostSuffix, StringComparison.OrdinalIgnoreCase)
                   || host.EndsWith(AwsChinaHostSuffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Reports whether an AWS host is one Bedrock answers on.
    /// </summary>
    /// <remarks>
    ///     An AWS host is not a Bedrock host. s3.eu-central-1.amazonaws.com and sts.amazonaws.com both end the
    ///     same way, and accepting them sent a Bedrock credential to a service that never serves models.
    /// </remarks>
    /// <param name="host">The host component of the address.</param>
    public static bool IsBedrockHost(string? host)
    {
        if (!IsAwsHost(host))
        {
            return false;
        }

        // Any label, not just the first. An interface VPC endpoint puts the endpoint id in front of the service,
        // as in vpce-0a1b2c3d-abcdefgh.bedrock-runtime.eu-central-1.vpce.amazonaws.com, and reading only the
        // first label refused an address BedrockClientFactory goes on to handle as a PrivateLink ServiceURL.
        return host!.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Any(label => BedrockServiceLabels.Contains(label, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Reports the region named twice by an endpoint that names it twice and disagrees with itself, or null
    ///     when there is no conflict.
    /// </summary>
    /// <remarks>
    ///     The query parameter used to win outright. A URL naming one region with a parameter naming another
    ///     then signed for the parameter's region and sent to the host's, which is a residency question an
    ///     operator cannot see the answer to. Reported so the save refuses instead.
    /// </remarks>
    /// <param name="endpoint">The stored provider endpoint.</param>
    public static (string FromHost, string FromParameter)? DescribeRegionConflict(ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint.DefaultQueryParams is not { } parameters
            || !parameters.TryGetValue(RegionParameterName, out var configured)
            || string.IsNullOrWhiteSpace(configured)
            || !Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var uri)
            || RegionFromHost(uri.Host) is not { } fromHost)
        {
            return null;
        }

        return string.Equals(fromHost, configured.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : (fromHost, configured.Trim());
    }


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
            // The label after the region is 'amazonaws' on a public host and 'vpce' on an interface VPC
            // endpoint, so both forms report the region the operator's address names.
            if (LooksLikeRegion(labels[index])
                && (labels[index + 1].Equals("amazonaws", StringComparison.OrdinalIgnoreCase)
                    || labels[index + 1].Equals("vpce", StringComparison.OrdinalIgnoreCase)))
            {
                return labels[index].ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>
    ///     Reads the Amazon Bedrock API key an endpoint carries, or null when it carries none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The key is asked for under the host's own single-secret field name, so a credential of one field
    ///         is stored as its value alone and read back either under that name or as the endpoint's secret.
    ///         Both are read for that reason.
    ///     </para>
    ///     <para>
    ///         A value in the packed <c>accessKeyId:secretAccessKey</c> shape is refused rather than returned.
    ///         The superseded access-key mode asks for its pair under the same field name, and the host carries
    ///         a stored credential across a change of authentication mode so a profile can be edited without
    ///         re-entering it. Without this, moving a profile onto the bearer mode would send the pair to AWS as
    ///         a bearer token, which fails as an authentication error and says nothing about the cause.
    ///     </para>
    /// </remarks>
    /// <param name="endpoint">The stored provider endpoint.</param>
    public static string? ResolveBearerToken(ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!ProviderVocabulary.ValuesEqual(endpoint.AuthMode, BedrockProviderDriver.ApiKeyAuth))
        {
            return null;
        }

        return Declared(endpoint, ProviderCredentialField.ApiKeyFieldName)
               ?? (string.IsNullOrWhiteSpace(endpoint.Secret) ? null : endpoint.Secret.Trim());
    }

    /// <summary>Reports whether an endpoint carries a credential this family can sign with.</summary>
    /// <param name="endpoint">The stored provider endpoint.</param>
    public static bool HasCredential(ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return ResolveBearerToken(endpoint) is not null;
    }

    private static string? Declared(ProviderEndpoint endpoint, string name)
    {
        return endpoint.DeclaredValues.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
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
