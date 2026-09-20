// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Resilience;
using Microsoft.Extensions.AI;
using MeisterDev.Ai.Providers.Usage;

namespace MeisterDev.Ai.Providers.BedrockAddIn;

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
///         Unlike a family that speaks its vendor's protocol itself, this one is built on the official AWS
///         adapter. SigV4 signing is not something to reimplement — getting it subtly wrong fails in ways that
///         look like permission problems — and the adapter already maps the Converse shape onto the same seam
///         every other driver answers.
///     </para>
///     <para>
///         What the installation permits an address to reach arrives on the probe target, because this family is
///         constructed with no arguments and has no other way to learn it; the host applies the same rule before
///         this family is asked, and the connect-time address check on every client the host hands out applies it
///         again.
///     </para>
/// </remarks>
public sealed class BedrockProviderDriver : IAiProviderDriver
{
    /// <summary>The identity key every connection of this family is stored against.</summary>
    public const string FamilyKey = "meisterdev/awsBedrock";

    /// <summary>The protocol mode this family owns: the Bedrock Converse API.</summary>
    public const string ConverseProtocol = FamilyKey + ":BedrockConverse";

    /// <summary>
    ///     The Amazon Bedrock API key: a bearer token AWS issues against an IAM identity, sent as
    ///     <c>Authorization: Bearer</c> rather than used to sign the request.
    /// </summary>
    public const string ApiKeyAuth = FamilyKey + ":ApiKey";

    /// <summary>The name the AWS adapter reports the cache-write bucket under.</summary>
    private const string CacheWriteCountName = "CacheWriteInputTokens";

    /// <summary>
    ///     Usage recorded from a Converse call served largely from cache, as the AWS adapter hands the counts
    ///     over: 120 real prompt tokens beside a 4,000-token cache read and a 50-token cache write.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The driver checks replay this through <c>ReadUsage</c> and assert the counter relationship on the
    ///         result: the input total covers both cache buckets and the output total covers the reasoning
    ///         portion. The host bills the input total less the two cache buckets, floored at zero, so a mapping
    ///         that returns a vendor's counts unchanged where the vendor reports input exclusive of them bills
    ///         the whole prompt at nothing. The payload is recorded from the vendor and not written by hand,
    ///         because a synthesised one would restate the assumption of whoever wrote the mapping.
    ///     </para>
    ///     <para>
    ///         Converse reports its input count exclusive of both cache buckets, so the mapping adds them back
    ///         in. This payload is what it looks like before it does.
    ///     </para>
    /// </remarks>
    internal const string RecordedUsagePayload =
        "{\"inputTokenCount\":120,\"outputTokenCount\":207,\"totalTokenCount\":4377,"
        + "\"cachedInputTokenCount\":4000,\"additionalCounts\":{\"CacheWriteInputTokens\":50}}";

    private readonly IBedrockClientFactory _clientFactory;

    /// <summary>Builds the family as a host loading it from a directory does, with no arguments.</summary>
    public BedrockProviderDriver()
        : this(new BedrockClientFactory())
    {
    }

    /// <summary>Builds the family over a supplied client factory.</summary>
    /// <param name="clientFactory">Source of the AWS clients a profile is served by.</param>
    /// <remarks>
    ///     The AWS clients reach the network in their constructor's shadow, so this seam is what lets the
    ///     family's behaviour be exercised without an AWS account.
    /// </remarks>
    public BedrockProviderDriver(IBedrockClientFactory clientFactory)
    {
        this._clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    /// <summary>
    ///     What this family is, stated once. It declares no actions: a connection's regional endpoint and
    ///     verification state are host columns.
    /// </summary>
    private static readonly ProviderDeclaration Declared = new()
    {
        Key = FamilyKey,
        Label = "AWS Bedrock",
        Version = "1.0",
        ContractVersion = ProviderContract.Version,
        LegacyNames = ProviderLegacyNames.FromUnqualifiedNames(
            ["AwsBedrock"],
            [ApiKeyAuth],
            [ConverseProtocol]),

        AuthModes = [new ProviderDeclaredAuthMode(ApiKeyAuth, [AiCredentialFieldSupport.ApiKey]) { Label = "API Key" }],
        ProtocolModes = new ProviderDeclaredProtocolModes(
        [
            ProviderDeclaredProtocolModes.Auto,
            ConverseProtocol,
            ProviderDeclaredProtocolModes.Embeddings,
        ]),
        ConnectionForm = new ProviderConnectionForm(
            NamePlaceholder: "Bedrock (eu-central-1)",
            BaseUrlPlaceholder: "https://bedrock-runtime.eu-central-1.amazonaws.com",

            // The region is in the hostname rather than in a setting, so the address is what an operator with a
            // residency requirement has to get right.
            BaseUrlHint: "The host names the region inference runs in, and that pins where the data goes.",
            QueryParamPlaceholder: "region=eu-central-1"),

        // An AWS service host is <service>.<region>.amazonaws.com, and the region in the middle is the
        // operator's, which is why these are suffixes and not hosts. The second covers the China partition.
        ReachedHostPatterns = [".amazonaws.com", ".amazonaws.com.cn"],
        ConformanceInputs = new ProviderConformanceInputs(ApiKeyAuth, RecordedUsagePayload: RecordedUsagePayload),
    };

    /// <inheritdoc />
    public ProviderDeclaration Declaration => Declared;


    /// <inheritdoc />
    public IReadOnlyList<string> SupportedProtocolModes => Declared.ProtocolModes.Supported;

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedAuthModes => Declared.SupportedAuthModes;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<ProviderCredentialField>> CredentialFields =>
        Declared.CredentialFields;

    /// <inheritdoc />
    /// <remarks>
    ///     The endpoint has to name its region, because that is where the inference happens and a profile whose
    ///     region is implicit cannot be checked against a residency requirement.
    /// </remarks>
    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (ProbeTargetChecks.AbsoluteUrl(target, out var uri) is { } urlError)
        {
            return urlError;
        }

        if (ProbeTargetChecks.Egress(uri, target) is { } egressError)
        {
            return egressError;
        }

        // A host outside AWS is a private or VPC endpoint, which the operator opts into. An AWS host has to be
        // one Bedrock answers on: s3 and sts end the same way and never serve models.
        var isAwsHost = BedrockEndpointResolution.IsAwsHost(uri.Host);
        if (!isAwsHost && !target.AllowsPrivateAddress)
        {
            return "An AWS Bedrock connection must target an AWS host, for example "
                   + "https://bedrock-runtime.eu-central-1.amazonaws.com.";
        }

        if (isAwsHost && !BedrockEndpointResolution.IsBedrockHost(uri.Host))
        {
            return "An AWS Bedrock connection must target a Bedrock host, for example "
                   + "https://bedrock-runtime.eu-central-1.amazonaws.com.";
        }

        if (isAwsHost && BedrockEndpointResolution.RegionFromHost(uri.Host) is null)
        {
            return "The endpoint must name its region, for example https://bedrock-runtime.eu-central-1.amazonaws.com.";
        }

        if (!ProviderVocabulary.Names([ApiKeyAuth], target.AuthMode))
        {
            return "AWS Bedrock takes an API key; choose the API key authentication mode.";
        }

        // The ambient AWS credential chain is deliberately not a fallback here: in a multi-tenant deployment it
        // is the operator's identity, not the tenant's, so a profile without its own key is refused rather than
        // quietly served by someone else's role.
        return target.HasApiKey ? null : "An Amazon Bedrock API key is required.";
    }

    /// <inheritdoc />
    public async Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        IAmazonBedrock? created;
        try
        {
            created = this._clientFactory.CreateControlPlaneClient(endpoint);
        }
        catch (InvalidOperationException configuration)
        {
            // A region that cannot be read, a secret that is not an access-key pair, or a host that supplied no
            // transport is a configuration mistake. Verification names it the same way, and discovery reaching
            // the caller as an exception instead would arrive as an internal error naming nothing.
            return new ProviderModelDiscoveryResult("failed", true, [configuration.Message], []);
        }

        using var control = created;
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
        catch (Exception failure) when (IsCallFailure(failure))
        {
            return new ProviderModelDiscoveryResult("failed", true, [failure.Message], []);
        }
    }

    /// <inheritdoc />
    public async Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        IAmazonBedrock? control;
        try
        {
            control = this._clientFactory.CreateControlPlaneClient(endpoint);
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
        catch (Exception failure) when (IsCallFailure(failure))
        {
            return DriverFailureMapper.Failed(failure);
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
        string protocolMode)
    {
        ArgumentNullException.ThrowIfNull(model);
        AiProtocolModeSupport.Require(Declared.Key, this.SupportedProtocolModes, protocolMode);

        if (this._clientFactory.CreateRuntimeClient(endpoint) is not { } runtime)
        {
            return UnreachableClients.ChatClient("AWS Bedrock");
        }

        return new BedrockConverseChatClient(runtime.AsIChatClient(model.RemoteModelId), model.SupportsPromptCaching);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         Converse reports <c>inputTokens</c> exclusive of the two cache buckets, so both are added back into
    ///         the input total. Without that the host bills the input total less the buckets, which for a call
    ///         served largely from cache is a negative number floored at zero: a 4,000-token cache read beside 120
    ///         real prompt tokens bills those 120 at nothing.
    ///     </para>
    ///     <para>
    ///         The AWS adapter maps the cache-read bucket onto the standard property and leaves the write bucket
    ///         under Converse's own name, which is the one vendor spelling this family reads.
    ///     </para>
    /// </remarks>
    public ProviderTokenUsage ReadUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return ProviderTokenUsage.Missing;
        }

        var cacheRead = usage.CachedInputTokenCount ?? 0;
        var cacheWrite = usage.AdditionalCounts?.TryGetValue(CacheWriteCountName, out var written) == true ? written : 0;

        return new ProviderTokenUsage(
            (usage.InputTokenCount ?? 0) + cacheRead + cacheWrite,
            usage.OutputTokenCount ?? 0,
            cacheRead,
            cacheWrite,
            usage.ReasoningTokenCount ?? 0);
    }

    /// <inheritdoc />
    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode)
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
        string protocolMode,
        int dimensions)
    {
        ArgumentNullException.ThrowIfNull(model);
        _ = protocolMode;

        if (this._clientFactory.CreateRuntimeClient(endpoint) is not { } runtime)
        {
            return UnreachableClients.EmbeddingGenerator("AWS Bedrock");
        }

        return runtime.AsIEmbeddingGenerator(model.RemoteModelId, dimensions > 0 ? dimensions : null);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The AWS SDK reports throttling and capacity through its own exception type rather than as an HTTP
    ///     failure the shared rule would recognise, so those are classified here and everything else is left to
    ///     the shared rule. It has to be recognised in the assembly that references the library for a second
    ///     reason: this family resolves the AWS SDK from its own folder, so an exception it raises is of a type
    ///     the host's own copy would not match.
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

    /// <summary>
    ///     Whether a call to AWS failed in a way that belongs in a result rather than to the caller.
    /// </summary>
    /// <remarks>
    ///     <see cref="AmazonServiceException" /> is what AWS raises once a request reached the service, and it
    ///     carries a status. The three here got no answer, and none of them derives from that type in this
    ///     version of the SDK: <see cref="AmazonClientException" /> is a failure of the SDK's own — credentials
    ///     it cannot resolve, a dependency it cannot load — while a connection it could not make surfaces as the
    ///     transport's <see cref="HttpRequestException" /> and a connection cut mid-response as
    ///     <see cref="IOException" />. An unreachable or egress-blocked endpoint is an ordinary
    ///     misconfiguration, so it belongs in a failed result naming it rather than in an internal error.
    /// </remarks>
    /// <param name="failure">The exception the call threw.</param>
    private static bool IsCallFailure(Exception failure)
    {
        return failure is AmazonClientException or HttpRequestException or IOException;
    }

    /// <summary>
    ///     Maps one listed foundation model, or <see langword="null" /> for the ones this system cannot use.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Bedrock reports what a model takes in and puts out, but says nothing about tool use or structured
    ///         output. Rather than guess per model, text models are offered as tool-capable — that is what the
    ///         review loop needs, and a model that turns out not to be says so on its first call — while
    ///         structured output is left unclaimed because the adapter reaches it through a tool anyway.
    ///     </para>
    ///     <para>
    ///         A summary may list both output modalities, and each one carries its own protocol mode, so both are
    ///         kept. Dropping either would leave the model unbindable for that purpose, since a purpose is bound
    ///         against the operation kinds and the protocol modes discovery recorded.
    ///     </para>
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

        List<AiOperationKind> operations = [];
        List<string> protocolModes = [ProviderDeclaredProtocolModes.Auto];

        if (isText)
        {
            operations.Add(AiOperationKind.Chat);
            protocolModes.Add(ConverseProtocol);
        }

        if (isEmbedding)
        {
            operations.Add(AiOperationKind.Embedding);
            protocolModes.Add(ProviderDeclaredProtocolModes.Embeddings);
        }

        return new ProviderDiscoveredModel(
            summary.ModelId,
            string.IsNullOrWhiteSpace(summary.ModelName) ? summary.ModelId : $"{summary.ProviderName} {summary.ModelName}".Trim(),
            operations,
            protocolModes,
            SupportsStructuredOutput: false,
            SupportsToolUse: isText);
    }
}
