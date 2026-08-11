// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Resilience;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Amazon Bedrock, reached natively through the Converse API in the account and region the profile names.
/// </summary>
/// <remarks>
///     <para>
///         The reason to speak to Bedrock directly rather than through a gateway is residency: a customer who
///         requires inference inside their own AWS tenancy and region needs the call to be made there, with their
///         credentials, and needs to be able to see that it is. A proxy in the middle makes that unprovable.
///     </para>
///     <para>
///         Unlike the Anthropic driver this one is built on the official AWS adapter rather than on the protocol.
///         SigV4 signing is not something to reimplement — getting it subtly wrong fails in ways that look like
///         permission problems — and the adapter already maps the Converse shape onto the same seam every other
///         driver answers.
///     </para>
/// </remarks>
public sealed class BedrockProviderDriver(
    IBedrockClientFactory clientFactory,
    bool allowPrivateEgress,
    bool allowInsecureScheme) : IAiProviderDriver
{
    /// <inheritdoc />
    public AiProviderKind ProviderKind => AiProviderKind.AwsBedrock;

    /// <inheritdoc />
    public IReadOnlyList<AiProtocolMode> SupportedProtocolModes { get; } =
        [AiProtocolMode.Auto, AiProtocolMode.BedrockConverse, AiProtocolMode.Embeddings];

    /// <inheritdoc />
    /// <remarks>
    ///     The endpoint has to name its region, because that is where the inference happens and a profile whose
    ///     region is implicit cannot be checked against a residency requirement.
    /// </remarks>
    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        if (ProbeTargetChecks.AbsoluteUrl(target, out var uri) is { } urlError)
        {
            return urlError;
        }

        if (ProbeTargetChecks.Egress(uri, allowPrivateEgress, allowInsecureScheme) is { } egressError)
        {
            return egressError;
        }

        // Anything that is not an AWS host is a private or VPC endpoint, which is the operator opt-in's business
        // rather than the default. On an AWS host the region is read from the host itself.
        var isAwsHost = uri.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
                        || uri.Host.EndsWith(".amazonaws.com.cn", StringComparison.OrdinalIgnoreCase);
        if (!isAwsHost && !allowPrivateEgress)
        {
            return "An AWS Bedrock connection must target an AWS host, for example "
                   + "https://bedrock-runtime.eu-central-1.amazonaws.com.";
        }

        if (isAwsHost && BedrockEndpointResolution.RegionFromHost(uri.Host) is null)
        {
            return "The endpoint must name its region, for example https://bedrock-runtime.eu-central-1.amazonaws.com.";
        }

        if (target.AuthMode != AiAuthMode.SigV4 && target.AuthMode != AiAuthMode.ApiKey)
        {
            return "AWS Bedrock signs its requests with an access key; choose the API key or SigV4 authentication mode.";
        }

        // The ambient AWS credential chain is deliberately not a fallback here: in a multi-tenant deployment it
        // is the operator's identity, not the tenant's, so a profile without its own key is refused rather than
        // quietly served by someone else's role.
        return target.HasApiKey
            ? null
            : "An AWS access key is required. Store it as 'accessKeyId:secretAccessKey'.";
    }

    /// <inheritdoc />
    public async Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        using var control = this.CreateControlPlane(endpoint);
        if (control is null)
        {
            return new ProviderModelDiscoveryResult(
                "succeeded",
                true,
                [PrivateEndpointNotice],
                []);
        }

        try
        {
            var listed = await control.ListFoundationModelsAsync(new ListFoundationModelsRequest(), ct).ConfigureAwait(false);
            var models = (listed.ModelSummaries ?? []).Select(ToDiscoveredModel).OfType<ProviderDiscoveredModel>().ToList();

            return new ProviderModelDiscoveryResult(
                "succeeded",
                true,
                models.Count == 0
                    ? ["No models were discovered from the provider. Manual model entry remains available."]
                    : [InferenceProfileNotice],
                models);
        }
        catch (AmazonServiceException failure)
        {
            return new ProviderModelDiscoveryResult("failed", true, [Describe(failure)], []);
        }
    }

    /// <inheritdoc />
    public async Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        IAmazonBedrock? control;
        try
        {
            control = this.CreateControlPlane(endpoint);
        }
        catch (InvalidOperationException configuration)
        {
            // A region that cannot be read or a secret that is not an access-key pair is a configuration
            // mistake, and naming it is more useful than a signing failure from AWS would be.
            return DriverFailureMapper.Failed(HttpStatusCode.BadRequest, configuration.Message);
        }

        if (control is null)
        {
            // Nothing was called, so nothing may be claimed beyond what was checked. Saying which is which
            // keeps "Verified" from meaning two different things depending on the endpoint.
            return DriverFailureMapper.Verified(
                $"Accepted the Bedrock configuration for '{endpoint.BaseUrl}'.",
                [PrivateEndpointNotice]);
        }

        try
        {
            var listed = await control.ListFoundationModelsAsync(new ListFoundationModelsRequest(), ct).ConfigureAwait(false);
            var count = listed.ModelSummaries?.Count ?? 0;

            return DriverFailureMapper.Verified(
                $"Verified AWS Bedrock access in '{BedrockEndpointResolution.ResolveRegion(endpoint)}' ({count} models).",
                count == 0 ? ["No models were discovered from the provider. Manual model entry remains available."] : []);
        }
        catch (AmazonServiceException failure)
        {
            return DriverFailureMapper.Failed(failure.StatusCode, Describe(failure));
        }
        finally
        {
            control.Dispose();
        }
    }

    /// <inheritdoc />
    public IChatClient CreateChatClient(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode)
    {
        ArgumentNullException.ThrowIfNull(model);
        AiProtocolModeSupport.Require(this.ProviderKind, this.SupportedProtocolModes, protocolMode);

        var runtime = clientFactory.CreateRuntimeClient(endpoint);

        return new BedrockConverseChatClient(runtime.AsIChatClient(model.RemoteModelId), model.SupportsPromptCaching);
    }

    /// <inheritdoc />
    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode)
    {
        ArgumentNullException.ThrowIfNull(model);

        _ = endpoint;
        _ = protocolMode;

        // Caching is claimed per model rather than per provider: on Bedrock it is the model that supports it, and a
        // cache point sent to one that does not is a rejected request rather than a wasted marker. The host states
        // which models qualify, and the read side is already in place - Bedrock reports its cache buckets in usage.
        return ProviderRuntimeCapabilities.None with { SupportsPromptCaching = model.SupportsPromptCaching };
    }

    /// <inheritdoc />
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode,
        int dimensions)
    {
        ArgumentNullException.ThrowIfNull(model);

        var runtime = clientFactory.CreateRuntimeClient(endpoint);

        return runtime.AsIEmbeddingGenerator(model.RemoteModelId, dimensions > 0 ? dimensions : null);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The AWS SDK reports throttling and capacity through its own exception type rather than as an HTTP
    ///     failure the shared rule would recognise, so those are classified here and everything else is left to
    ///     the shared rule.
    /// </remarks>
    public ProviderFailureVerdict ClassifyRuntimeFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var candidate = exception; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is not AmazonServiceException aws)
            {
                continue;
            }

            // Bedrock reports an exhausted quota by error code, so it is named as throttling rather than only as
            // transient. Every other caller on the same account is about to be refused too, and only a verdict
            // that says "throttled" lets a later stage act on that.
            var isThrottled = aws.ErrorCode is "ThrottlingException" or "TooManyRequestsException"
                              || (int)aws.StatusCode == 429;

            // A model that is still warming up answers this way and is worth waiting for, as is anything the
            // service failed internally.
            var isTransient = isThrottled
                              || aws.ErrorCode is "ModelNotReadyException" or "ServiceUnavailableException"
                                  or "InternalServerException" or "ModelTimeoutException"
                              || (int)aws.StatusCode >= 500;

            var reason = Describe(aws);
            if (isThrottled)
            {
                return ProviderFailureVerdict.Throttled(reason, null, (int)aws.StatusCode);
            }

            return isTransient
                ? ProviderFailureVerdict.Transient(reason, null, (int)aws.StatusCode)
                : ProviderFailureVerdict.Permanent(reason, (int)aws.StatusCode);
        }

        return DriverFailureMapper.ClassifyRuntimeFailure(exception);
    }

    private const string PrivateEndpointNotice =
        "Model discovery is only available on an AWS host; for a private or VPC endpoint, enter the models manually.";

    private const string InferenceProfileNotice =
        "Some Bedrock models can only be called through an inference profile. Where the account requires one, "
        + "use the profile ID as the model ID.";

    private static string Describe(AmazonServiceException failure)
    {
        return string.IsNullOrWhiteSpace(failure.ErrorCode)
            ? failure.Message
            : $"{failure.ErrorCode}: {failure.Message}";
    }

    private IAmazonBedrock? CreateControlPlane(ProviderEndpoint endpoint)
    {
        return clientFactory.CreateControlPlaneClient(endpoint);
    }

    /// <summary>
    ///     Maps one listed foundation model, or <see langword="null" /> for the ones this system cannot use.
    /// </summary>
    /// <remarks>
    ///     Bedrock reports what a model takes in and puts out, but says nothing about tool use or structured
    ///     output. Rather than guess per model, text models are offered as tool-capable — that is what the review
    ///     loop needs, and a model that turns out not to be says so on its first call — while structured output
    ///     is left unclaimed because the adapter reaches it through a tool anyway.
    /// </remarks>
    private static ProviderDiscoveredModel? ToDiscoveredModel(FoundationModelSummary summary)
    {
        var outputs = summary.OutputModalities ?? [];
        var isText = outputs.Any(modality => string.Equals(modality, "TEXT", StringComparison.OrdinalIgnoreCase));
        var isEmbedding = outputs.Any(modality => string.Equals(modality, "EMBEDDING", StringComparison.OrdinalIgnoreCase));

        if (!isText && !isEmbedding)
        {
            return null;
        }

        return new ProviderDiscoveredModel(
            summary.ModelId,
            string.IsNullOrWhiteSpace(summary.ModelName) ? summary.ModelId : $"{summary.ProviderName} {summary.ModelName}".Trim(),
            isEmbedding ? [AiOperationKind.Embedding] : [AiOperationKind.Chat],
            isEmbedding
                ? [AiProtocolMode.Auto, AiProtocolMode.Embeddings]
                : [AiProtocolMode.Auto, AiProtocolMode.BedrockConverse],
            SupportsStructuredOutput: false,
            SupportsToolUse: isText);
    }
}
