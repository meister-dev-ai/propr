// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights.Taxonomy;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Ports;

/// <summary>
///     One finding presented for classification, with the vocabulary it may be classified against.
/// </summary>
/// <param name="ClientId">The client whose model and vocabulary apply.</param>
/// <param name="FindingId">The finding's surrogate identity.</param>
/// <param name="Message">The finding text.</param>
/// <param name="FilePath">File the finding is anchored to, or <c>null</c> for a pull-request-level finding.</param>
/// <param name="LineNumber">Line the finding is anchored to, or <c>null</c> when unknown.</param>
/// <param name="Severity">Severity the review assigned.</param>
/// <param name="OriginPassKind">
///     The review pass that produced the finding. A pull-request-wide pass is evidence that the finding spans
///     more than one file, which is the main signal available for the level axis.
/// </param>
/// <param name="Vocabulary">
///     The core types plus the client's <em>active</em> custom tags. The classifier may return nothing outside
///     this set.
/// </param>
public sealed record FindingClassificationRequest(
    Guid ClientId,
    Guid FindingId,
    string Message,
    string? FilePath,
    int? LineNumber,
    CommentSeverity Severity,
    string? OriginPassKind,
    CodeInsightTaxonomyDto Vocabulary);

/// <summary>
///     What the classifier concluded about one finding. Slugs and tag ids are guaranteed to come from the
///     vocabulary that was supplied: an out-of-vocabulary label is dropped rather than invented into
///     existence, because a tag nothing defined would be an aggregate nobody could interpret.
/// </summary>
/// <param name="CoreSlugs">Core types that apply; at least one when the classification succeeded.</param>
/// <param name="CustomTagIds">The client's custom tags that apply, possibly none.</param>
/// <param name="Level">The level of code the finding concerns.</param>
/// <param name="Qualifier">Whether the code in question is absent, wrong, or unnecessary.</param>
/// <param name="Confidence">
///     The classifier's own confidence, 0–1. Stored so the evaluation story can calibrate a threshold later
///     without re-running the model.
/// </param>
public sealed record FindingTypeVerdict(
    IReadOnlyList<string> CoreSlugs,
    IReadOnlyList<Guid> CustomTagIds,
    CodeInsightFindingLevel Level,
    CodeInsightFindingQualifier Qualifier,
    double Confidence);

/// <summary>
///     The result of presenting one finding to the classifier.
/// </summary>
/// <remarks>
///     A verdict of <see langword="null" /> is not one situation but two, and they deserve different handling.
///     A model that was asked and produced nothing usable has spent an attempt. A purpose with no model bound at
///     all was never asked, and charging it an attempt would let a configuration mistake quietly exhaust every
///     finding's retry allowance, after which fixing the configuration changes nothing, because the findings
///     have already been written off.
/// </remarks>
/// <param name="Verdict">What the classifier concluded, or <see langword="null" /> when nothing usable came back.</param>
/// <param name="ModelWasAsked">
///     Whether a model was actually consulted. <see langword="false" /> means the client has no model bound for
///     this purpose, so there was nothing to ask.
/// </param>
public sealed record FindingClassificationResult(FindingTypeVerdict? Verdict, bool ModelWasAsked)
{
    /// <summary>A successful classification.</summary>
    public static FindingClassificationResult Classified(FindingTypeVerdict verdict)
    {
        return new FindingClassificationResult(verdict, ModelWasAsked: true);
    }

    /// <summary>A model was asked and produced nothing usable. This has cost an attempt.</summary>
    public static FindingClassificationResult Unusable()
    {
        return new FindingClassificationResult(null, ModelWasAsked: true);
    }

    /// <summary>No model is bound for the purpose, so nothing was asked and nothing should be charged.</summary>
    public static FindingClassificationResult NoModelBound()
    {
        return new FindingClassificationResult(null, ModelWasAsked: false);
    }
}

/// <summary>
///     Post-hoc classifier that decides what kind of problem a finding describes. It runs off the review
///     path, so a failure here delays nothing and changes no review output.
/// </summary>
public interface IFindingTypeClassifier
{
    /// <summary>
    ///     Identifier of this classifier's prompt and parsing behaviour, stamped onto every assignment so a
    ///     later prompt change is visible in the data and a re-grade can tell one generation from another.
    /// </summary>
    string ClassifierVersion { get; }

    /// <summary>
    ///     Classifies one finding. Never throws except for cancellation; the result says whether a model was
    ///     asked, which is what lets the caller decide between spending an attempt and waiting for configuration.
    /// </summary>
    Task<FindingClassificationResult> ClassifyAsync(
        FindingClassificationRequest request,
        CancellationToken ct = default);
}
