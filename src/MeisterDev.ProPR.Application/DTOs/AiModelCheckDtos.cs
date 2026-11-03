// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>A configured model of a profile that a model check calls, and whether it is called for chat or embeddings.</summary>
/// <param name="ConfiguredModelId">The configured model of the profile.</param>
/// <param name="Operation">What the model is checked for.</param>
public sealed record AiModelCheckItem(Guid ConfiguredModelId, AiOperationKind Operation);

/// <summary>The configured models of a profile to check, at most <see cref="AiModelCheckReasons.MaximumModels" />.</summary>
/// <param name="Models">The models and what each one is checked for.</param>
public sealed record AiModelCheckRequest(IReadOnlyList<AiModelCheckItem>? Models);

/// <summary>What a model check found for one model.</summary>
/// <param name="ConfiguredModelId">The configured model.</param>
/// <param name="RemoteModelId">The identifier the provider knows the model by.</param>
/// <param name="Operation">What the model was checked for.</param>
/// <param name="Passed">Whether the model answered as the profile configures it.</param>
/// <param name="Reason">Why the check failed, one of <see cref="AiModelCheckReasons" />, or null when it passed.</param>
/// <param name="HttpStatus">The status the provider answered a failed call with, or null without one.</param>
/// <param name="Summary">What the provider or the check said about a failure, without credential material.</param>
/// <param name="ActionHint">What to try next after a failure.</param>
public sealed record AiModelCheckOutcomeDto(
    Guid ConfiguredModelId,
    string RemoteModelId,
    AiOperationKind Operation,
    bool Passed,
    string? Reason = null,
    int? HttpStatus = null,
    string? Summary = null,
    string? ActionHint = null);

/// <summary>The outcomes of a model check, in the order of the request.</summary>
public sealed record AiModelCheckResultDto(IReadOnlyList<AiModelCheckOutcomeDto> Results);

/// <summary>Why a model check failed. A caller maps these to its own words; the summary is the provider's.</summary>
public static class AiModelCheckReasons
{
    /// <summary>The most models one check calls.</summary>
    public const int MaximumModels = 3;

    /// <summary>The call got no answer: the address, the network, TLS or the time limit failed.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The provider refused the credential.</summary>
    public const string Credentials = "credentials";

    /// <summary>The model does not exist, or the credential may not use it.</summary>
    public const string ModelUnavailable = "modelUnavailable";

    /// <summary>The provider refused the request for another reason it stated.</summary>
    public const string Rejected = "rejected";

    /// <summary>The provider asked the caller to slow down or reported an exhausted quota.</summary>
    public const string RateLimited = "rateLimited";

    /// <summary>The provider failed while answering.</summary>
    public const string ProviderError = "providerError";

    /// <summary>The embedding model returned vectors of another size than the profile configures.</summary>
    public const string Dimensions = "dimensions";

    /// <summary>The cause is not known.</summary>
    public const string Unknown = "unknown";
}
