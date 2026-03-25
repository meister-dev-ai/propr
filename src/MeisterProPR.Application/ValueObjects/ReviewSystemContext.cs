using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;

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
}
