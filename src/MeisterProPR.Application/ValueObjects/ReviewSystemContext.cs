// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Application.ValueObjects;

/// <summary>
///     Carries all context required by the agentic review loop beyond the pull request itself.
///     Includes the optional client-level system message, relevant repository instructions,
///     and the tool provider the AI reviewer may invoke.
///     After <see cref="MeisterProPR.Application.Interfaces.IAiReviewCore.ReviewAsync" /> returns,
///     <see cref="LoopMetrics" /> is populated by <c>ToolAwareAiReviewCore</c> when available.
/// </summary>
public sealed class ReviewSystemContext
{
    /// <summary>
    ///     Initializes a new <see cref="ReviewSystemContext" />.
    /// </summary>
    /// <param name="clientSystemMessage">Optional custom AI system message configured for the client.</param>
    /// <param name="repositoryInstructions">Repository-specific instructions filtered for relevance to this pull request.</param>
    /// <param name="reviewTools">
    ///     Tool provider the AI reviewer may call during the review loop. May be <see langword="null" />
    ///     when tool use is not yet available.
    /// </param>
    public ReviewSystemContext(
        string? clientSystemMessage,
        IReadOnlyList<RepositoryInstruction> repositoryInstructions,
        IReviewContextTools? reviewTools)
    {
        this.ClientSystemMessage = clientSystemMessage;
        this.RepositoryInstructions = repositoryInstructions;
        this.ReviewTools = reviewTools;
    }

    /// <summary>Optional custom AI system message configured for the client.</summary>
    public string? ClientSystemMessage { get; }

    /// <summary>Repository-specific instructions filtered for relevance to this pull request.</summary>
    public IReadOnlyList<RepositoryInstruction> RepositoryInstructions { get; }

    /// <summary>
    ///     Tool provider the AI reviewer may call during the review loop. May be <see langword="null" /> when tool use is
    ///     not yet available for the current execution context.
    /// </summary>
    public IReviewContextTools? ReviewTools { get; }

    /// <summary>
    ///     Populated by <c>ToolAwareAiReviewCore</c> after the agentic review loop completes.
    ///     May be <see langword="null" /> for non-agentic reviews or when no metrics were collected.
    /// </summary>
    public ReviewLoopMetrics? LoopMetrics { get; set; }

    /// <summary>
    ///     The identifier of the active <c>ReviewJobProtocol</c> record created before the review started.
    ///     <see langword="null" /> when protocol recording is unavailable (e.g. DB write failed on begin).
    /// </summary>
    public Guid? ActiveProtocolId { get; set; }

    /// <summary>
    ///     When set, instructs the AI review core to use per-file prompt framing (US4).
    ///     Populated by <c>FileByFileReviewOrchestrator</c> before calling <c>IAiReviewCore.ReviewAsync</c>.
    ///     <see langword="null" /> for whole-PR reviews.
    /// </summary>
    public PerFileReviewHint? PerFileHint { get; set; }

    /// <summary>
    ///     The recorder used to persist individual protocol events during the review loop.
    ///     <see langword="null" /> when <see cref="ActiveProtocolId" /> is <see langword="null" />.
    /// </summary>
    public IProtocolRecorder? ProtocolRecorder { get; set; }

    /// <summary>
    ///     File exclusion rules for this repository. Files matching these rules are skipped before
    ///     dispatching to the AI review loop. Defaults to <see cref="ReviewExclusionRules.Empty" />.
    /// </summary>
    public ReviewExclusionRules ExclusionRules { get; init; } = ReviewExclusionRules.Empty;

    /// <summary>
    ///     Normalized pattern texts of findings that have been dismissed by the client admin.
    ///     Injected into the AI system prompt as exclusion rules so the AI does not re-report them.
    ///     Defaults to an empty list when no dismissals are configured or when dismissal loading fails.
    /// </summary>
    public IReadOnlyList<string> DismissedPatterns { get; init; } = [];

    /// <summary>
    ///     Per-prompt-key override texts loaded from the client's (and crawl-config's) persisted overrides.
    ///     Keys are the named prompt segment identifiers (e.g. <c>"AgenticLoopGuidance"</c>,
    ///     <c>"SynthesisSystemPrompt"</c>). Prompt builders consult this dictionary before returning the
    ///     global hardcoded constant, substituting the override text when present.
    ///     Defaults to an empty dictionary when no overrides are configured or when loading fails.
    /// </summary>
    public IReadOnlyDictionary<string, string> PromptOverrides { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    ///     When non-null, <c>ToolAwareAiReviewCore</c> uses this client instead of its injected default.
    ///     Set by <c>FileByFileReviewOrchestrator</c> when a tier-specific AI connection is configured
    ///     for the file's <see cref="PerFileReviewHint.ComplexityTier" />.
    /// </summary>
    public IChatClient? TierChatClient { get; set; }

    /// <summary>
    ///     The effective model deployment name to pass into <see cref="ChatOptions.ModelId" /> for the
    ///     current review stage. This lets client-scoped and tier-scoped AI connections override the
    ///     global fallback configured in <c>AiReviewOptions</c>.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    ///     The client-scoped default review chat client resolved before any tier-specific override is applied.
    ///     Comment relevance evaluation uses this runtime so it consistently follows the client's primary
    ///     review binding rather than the current file's complexity tier.
    /// </summary>
    public IChatClient? DefaultReviewChatClient { get; set; }

    /// <summary>
    ///     The client-scoped default review model deployment resolved before any tier-specific override is applied.
    /// </summary>
    public string? DefaultReviewModelId { get; set; }

    /// <summary>
    ///     Runtime capability flags resolved for the default review binding.
    /// </summary>
    public AgentReviewRuntimeCapabilities RuntimeCapabilities { get; set; } = new(false, false, false, false);

    /// <summary>
    ///     Optional logical review session carried across per-file multi-turn execution.
    /// </summary>
    public AgentReviewSession? ReviewSession { get; set; }

    /// <summary>
    ///     The effective review temperature to pass into chat calls for this job-scoped execution context.
    /// </summary>
    public float? Temperature { get; set; }

    /// <summary>
    ///     Controls whether ProRV-focused review guidance should run for this execution context.
    /// </summary>
    public bool EnableProRV { get; set; } = true;

    /// <summary>
    ///     Controls whether evidence-backed local verification escalates conservatively-withheld claims for this
    ///     execution context. When <see langword="false" /> the composite verifier behaves exactly like the
    ///     deterministic verifier.
    /// </summary>
    public bool EnableEvidenceBackedVerification { get; set; } = false;

    /// <summary>
    ///     Controls whether multi-pass union generation runs for this execution context. When <see langword="false" />
    ///     the per-file dispatch runs once and behavior is identical to a single-pass review. When <see langword="true" />
    ///     eligible files are reviewed across multiple independent passes whose findings are unioned before dedup.
    /// </summary>
    public bool EnableMultiPassUnion { get; set; } = false;

    /// <summary>
    ///     Eval-harness-only override for the number of independent per-file passes to run when
    ///     <see cref="EnableMultiPassUnion" /> is enabled and the file's resolved tier is in scope. When set, the
    ///     resample passes are driven by <see cref="MultiPassDiversity" /> arms over the tier connection. Production
    ///     reviews leave this <see langword="null" /> and instead drive the extra passes from <see cref="ReviewPasses" />.
    ///     Only consulted when <see cref="EnableMultiPassUnion" /> is <see langword="true" />.
    /// </summary>
    public int? MultiPassUnionPassCount { get; set; }

    /// <summary>
    ///     Ordered per-client review-pass list for production multi-pass union: each entry names a configured model
    ///     (its connection implied) that runs one additional pass after the implicit tier baseline, with an optional
    ///     specialist lens. Effective pass count is <c>1 + ReviewPasses.Count</c>. Empty means a single baseline pass.
    ///     Only consulted when <see cref="EnableMultiPassUnion" /> is <see langword="true" /> and
    ///     <see cref="MultiPassUnionPassCount" /> is <see langword="null" /> (the production path).
    /// </summary>
    public IReadOnlyList<ReviewPassSpec> ReviewPasses { get; init; } = [];

    /// <summary>
    ///     The specialist lens active for the current per-file pass context, or <see langword="null" /> for an
    ///     ordinary pass. Set on a lens pass (e.g. security) so prompt construction selects the specialist template.
    ///     Not persisted — a per-pass runtime marker set after the pass context is created.
    /// </summary>
    public string? ActiveLens { get; set; }

    /// <summary>
    ///     Diversity configuration for multi-pass union generation. <see langword="null" /> defers to
    ///     <see cref="Features.Reviewing.Execution.Models.MultiPassDiversity.Default" />. Only consulted when
    ///     <see cref="EnableMultiPassUnion" /> is <see langword="true" /> on the eval-harness path.
    /// </summary>
    public MultiPassDiversity? MultiPassDiversity { get; set; }

    /// <summary>
    ///     Controls whether review execution uses disabled, early-steering, or late-augmentation semantics.
    /// </summary>
    public ReviewAugmentationMode AugmentationMode { get; set; } = ReviewAugmentationMode.EarlySteering;

    /// <summary>
    ///     Identifies whether the current execution context is the baseline review pass or a ProRV augmentation pass.
    /// </summary>
    public ReviewPassKind PassKind { get; set; } = ReviewPassKind.Baseline;

    /// <summary>
    ///     Offline-only prompt experiment context applied to this workflow execution.
    /// </summary>
    public PromptExperimentContext? PromptExperiment { get; init; }

    /// <summary>
    ///     Offline-only explicit step skips applied to this workflow execution.
    /// </summary>
    public ReviewStepSkips SkippedSteps { get; init; } = new();

    /// <summary>
    ///     Aggressiveness posture resolved from the active review pipeline profile.
    ///     Controls whether the certainty gate discards uncertain findings (Calm/Balanced)
    ///     or emits them with confidence for downstream ranking (Assertive).
    ///     Defaults to <see cref="ReviewAggressiveness.Balanced" />.
    /// </summary>
    public ReviewAggressiveness Aggressiveness { get; set; } = ReviewAggressiveness.Balanced;

    /// <summary>
    ///     Prepared local repository workspace bound to this review execution when available.
    /// </summary>
    public IReviewRepositoryWorkspace? ReviewWorkspace { get; set; }
}
