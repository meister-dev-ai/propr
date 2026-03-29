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
}
