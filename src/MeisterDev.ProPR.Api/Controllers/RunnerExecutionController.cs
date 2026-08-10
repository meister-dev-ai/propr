// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.IO.Compression;
using MeisterDev.ProPR.Api.Features.Reviewing.Runners;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>
///     The surface a registered runner talks to.
///     <para>
///         Every action is thin on purpose. Authorization against the lease and its generation, the
///         idempotency, the ordering, and the ceilings all live in the services these call, so nothing here
///         can accidentally implement a second version of any of it. What the controller adds is the
///         identity: it comes from the authenticated credential, never from the request body, because a
///         caller that could name its own identity could name somebody else's.
///     </para>
/// </summary>
[ApiController]
[Route("runners/execution")]
[Authorize(AuthenticationSchemes = RunnerAuthenticationDefaults.Scheme)]
public sealed class RunnerExecutionController(
    IRunnerToolProxy tools,
    IRunnerMemoryProxy memory,
    IRunnerAiRelay relay,
    IRunnerIngestService ingest,
    IRunnerFindingsIntake findings,
    IRunnerPriorResultsReader priorResults,
    IRunnerWorkspaceServer workspace,
    IGitUploadPackTransport transport) : ControllerBase
{
    /// <summary>Source-control metadata: the files this review's revision changed.</summary>
    [HttpPost("tools/changed-files")]
    public async Task<IActionResult> GetChangedFiles([FromBody] RunnerJobCallRequest request, CancellationToken ct)
    {
        if (!this.TryBuildCall(request, out var call, out var failure))
        {
            return failure!;
        }

        var result = await tools.GetChangedFilesAsync(call, ct);
        return ToolResponse(result, result.Value);
    }

    /// <summary>A repository-aware knowledge answer.</summary>
    [HttpPost("tools/knowledge")]
    public async Task<IActionResult> AskKnowledge([FromBody] RunnerKnowledgeRequest request, CancellationToken ct)
    {
        if (!this.TryBuildCall(request, out var call, out var failure))
        {
            return failure!;
        }

        var result = await tools.AskKnowledgeAsync(call, request.Question, ct);
        return ToolResponse(result, result.Value);
    }

    /// <summary>The discussion on a work item linked to the review.</summary>
    [HttpPost("tools/linked-item-discussion")]
    public async Task<IActionResult> GetLinkedItemDiscussion(
        [FromBody] RunnerLinkedItemRequest request,
        CancellationToken ct)
    {
        if (!this.TryBuildCall(request, out var call, out var failure))
        {
            return failure!;
        }

        var result = await tools.GetLinkedItemDiscussionAsync(call, request.ProviderKey, ct);
        return ToolResponse(result, result.Value);
    }

    /// <summary>Symbol-aware insight, through the code-knowledge service the executor cannot reach.</summary>
    [HttpPost("tools/symbol-insight")]
    public async Task<IActionResult> GetSymbolInsight([FromBody] RunnerSymbolInsightRequest request, CancellationToken ct)
    {
        if (!this.TryBuildCall(request, out var call, out var failure))
        {
            return failure!;
        }

        var result = await tools.GetSymbolInsightAsync(call, request.Symbol, request.QueryMode, request.MaxRelations, ct);
        return ToolResponse(result, result.Value);
    }

    /// <summary>The structured fields of a work item linked to the review.</summary>
    [HttpPost("tools/linked-item-details")]
    public async Task<IActionResult> GetLinkedItemDetails([FromBody] RunnerLinkedItemRequest request, CancellationToken ct)
    {
        if (!this.TryBuildCall(request, out var call, out var failure))
        {
            return failure!;
        }

        var result = await tools.GetLinkedItemDetailsAsync(call, request.ProviderKey, ct);
        return ToolResponse(result, result.Value);
    }

    /// <summary>Resolves a related link on a linked item into a full summary.</summary>
    [HttpPost("tools/resolve-linked-item")]
    public async Task<IActionResult> ResolveLinkedItem([FromBody] RunnerRelatedItemRequest request, CancellationToken ct)
    {
        if (!this.TryBuildCall(request, out var call, out var failure))
        {
            return failure!;
        }

        var result = await tools.ResolveLinkedItemAsync(call, request.RelatedTargetKey, ct);
        return ToolResponse(result, result.Value);
    }

    /// <summary>Reconsiders one file's draft result against the thread-memory store.</summary>
    [HttpPost("memory/reconsider")]
    public async Task<IActionResult> ReconsiderWithMemory(
        [FromBody] RunnerMemoryReconsiderRequest request,
        CancellationToken ct)
    {
        if (!this.TryBuildCall(request, out var call, out var failure))
        {
            return failure!;
        }

        var result = await memory.ReconsiderAsync(
            call,
            request.FilePath,
            request.ChangeExcerpt,
            new ReviewResult(request.DraftSummary ?? string.Empty, request.DraftComments),
            request.Temperature,
            ct);

        return ToolResponse(result, result.Value);
    }

    /// <summary>Relays one chat completion, charged and capped centrally.</summary>
    [HttpPost("ai/chat")]
    public async Task<IActionResult> Chat([FromBody] RunnerChatRequest request, CancellationToken ct)
    {
        if (!this.TryBuildCall(request, out var call, out var failure))
        {
            return failure!;
        }

        var result = await relay.CompleteAsync(
            call,
            new RunnerRelayRequest(
                request.LogicalModelName,
                request.Messages,
                Infrastructure.AI.RunnerRelayedChatOptions.ToChatOptions(request.Options),
                request.IdempotencyKey),
            ct);

        return result.Refusal switch
        {
            RunnerRelayRefusal.None => this.Ok(new { response = result.Response, softCapReached = result.SoftCapReached, replayed = result.Replayed }),
            RunnerRelayRefusal.BudgetHardCapReached => this.StatusCode(
                StatusCodes.Status402PaymentRequired,
                new RunnerContractError(RunnerContractError.BudgetCapReached, "The job's hard budget cap is reached.")),
            RunnerRelayRefusal.NotAuthorized => this.LeaseRefusal(result.CallRefusal),
            _ => this.Conflict(new RunnerContractError(RunnerContractError.LeaseNotHeld, "This job is not held open by this replica.")),
        };
    }

    /// <summary>Applies one batch of trace events, per-file results, and spend.</summary>
    [HttpPost("ingest")]
    public async Task<IActionResult> Ingest([FromBody] RunnerIngestRequest request, CancellationToken ct)
    {
        if (!this.TryBuildCall(request, out var call, out var failure))
        {
            return failure!;
        }

        var result = await ingest.IngestAsync(
            call,
            new RunnerIngestBatch(
                request.Sequence,
                request.IdempotencyKey,
                request.Events,
                request.FileResults,
                request.Spend),
            ct);

        return result.Outcome switch
        {
            // Both are success to the executor: a resend after a network failure is ordinary, and making it
            // an error would have every runner carrying error handling for the common case.
            RunnerIngestOutcome.Applied or RunnerIngestOutcome.AlreadyApplied =>
                this.Ok(new { outcome = result.Outcome.ToString(), expectedSequence = result.ExpectedSequence }),
            RunnerIngestOutcome.NotAuthorized => this.LeaseRefusal(result.CallRefusal),
            RunnerIngestOutcome.TooLarge => this.StatusCode(
                StatusCodes.Status413PayloadTooLarge,
                new RunnerContractError(RunnerContractError.PayloadTooLarge, "Split the batch and resend.")),
            // The expected sequence is the whole backpressure contract: resend from here.
            _ => this.Conflict(new { outcome = result.Outcome.ToString(), expectedSequence = result.ExpectedSequence }),
        };
    }

    /// <summary>
    ///     What this job already has reviewed, for an executor picking it up after a reclaim.
    ///     <para>
    ///         Read here rather than carried in the manifest: a completed file result holds its findings, so
    ///         a job most of the way through a large review would put all of them in every lease offer, and
    ///         only a reclaim ever needs them.
    ///     </para>
    /// </summary>
    [HttpPost("prior-results")]
    public async Task<IActionResult> GetPriorResults([FromBody] RunnerJobCallRequest request, CancellationToken ct)
    {
        if (!this.TryBuildCall(request, out var call, out var failure))
        {
            return failure!;
        }

        var result = await priorResults.ReadAsync(call, ct);
        return ToolResponse(result, result.Value);
    }

    /// <summary>Submits one chunk of the review's findings for the control plane to publish.</summary>
    [HttpPost("findings")]
    public async Task<IActionResult> SubmitFindings([FromBody] RunnerFindingsRequest request, CancellationToken ct)
    {
        if (!this.TryBuildCall(request, out var call, out var failure))
        {
            return failure!;
        }

        var result = await findings.SubmitAsync(
            call,
            new RunnerFindingsChunk(
                request.SubmissionId,
                request.ChunkIndex,
                request.ChunkCount,
                request.Summary,
                request.Comments,
                request.Annotations),
            ct);

        return result.Outcome switch
        {
            RunnerSubmissionOutcome.Published or RunnerSubmissionOutcome.AlreadyPublished or
                RunnerSubmissionOutcome.AwaitingChunks =>
                this.Ok(new { outcome = result.Outcome.ToString(), missingChunks = result.MissingChunks }),
            RunnerSubmissionOutcome.NotAuthorized => this.LeaseRefusal(result.CallRefusal),
            _ => this.BadRequest(new RunnerContractError("submission_rejected", result.Reason ?? "Rejected.")),
        };
    }

    /// <summary>Where to fetch this job's repository content from, if the fetch is allowed.</summary>
    [HttpPost("workspace")]
    public async Task<IActionResult> Workspace([FromBody] RunnerJobCallRequest request, CancellationToken ct)
    {
        if (!this.TryBuildCall(request, out var call, out var failure))
        {
            return failure!;
        }

        var grant = await workspace.AuthorizeFetchAsync(call, ct);
        return grant.Refusal switch
        {
            RunnerWorkspaceRefusal.None => this.Ok(
                new { headSha = grant.Source!.HeadSha, baseSha = grant.Source.BaseSha, maxTransferBytes = grant.Source.MaxTransferBytes }),
            RunnerWorkspaceRefusal.NotAuthorized => this.LeaseRefusal(grant.CallRefusal),
            _ => this.Conflict(new RunnerContractError("workspace_unavailable", grant.Reason ?? "Unavailable.")),
        };
    }

    /// <summary>
    ///     The ref advertisement a git client reads first. Authorized per lease like everything else, so a
    ///     runner that lost the job cannot keep pulling its repository.
    /// </summary>
    [HttpGet("workspace/{jobId:guid}/{generation:int}/info/refs")]
    public async Task<IActionResult> AdvertiseRefs(
        Guid jobId,
        int generation,
        [FromQuery] string? service,
        CancellationToken ct)
    {
        if (!string.Equals(service, "git-upload-pack", StringComparison.Ordinal))
        {
            // Only fetching is offered. Push would be a runner writing to the mirror the control plane
            // owns, which nothing in this design needs and everything about it argues against.
            return this.BadRequest(new RunnerContractError("unsupported_git_service", "Only git-upload-pack is served."));
        }

        var grant = await this.AuthorizeWorkspaceAsync(jobId, generation, ct);
        if (grant.Source is null)
        {
            return this.WorkspaceRefusal(grant);
        }

        this.Response.ContentType = "application/x-git-upload-pack-advertisement";
        await transport.AdvertiseRefsAsync(grant.Source.MirrorPath, this.Response.Body, ct);
        return new EmptyResult();
    }

    /// <summary>Serves the client's negotiation and streams back the pack.</summary>
    [HttpPost("workspace/{jobId:guid}/{generation:int}/git-upload-pack")]
    public async Task<IActionResult> UploadPack(Guid jobId, int generation, CancellationToken ct)
    {
        var grant = await this.AuthorizeWorkspaceAsync(jobId, generation, ct);
        if (grant.Source is null)
        {
            return this.WorkspaceRefusal(grant);
        }

        this.Response.ContentType = "application/x-git-upload-pack-result";

        // Git compresses this request once the negotiation is big enough to be worth it, and says so in
        // Content-Encoding. ASP.NET does not decompress request bodies, so piping the raw stream hands
        // upload-pack gzip bytes where it expects pkt-lines; it fails with "bad line length character",
        // which reads like a corrupt client rather than an undecoded body. A fetch small enough to stay
        // uncompressed works, which is why this only appears once a repository has some refs.
        await using var body = Decoded(this.Request);
        await transport.UploadPackAsync(grant.Source.MirrorPath, body, this.Response.Body, ct);
        return new EmptyResult();
    }

    /// <summary>
    ///     The request body as upload-pack needs to read it, decompressed when the client compressed it.
    /// </summary>
    private static Stream Decoded(HttpRequest request)
    {
        var encoding = request.Headers.ContentEncoding.ToString();

        if (encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            return new GZipStream(request.Body, CompressionMode.Decompress);
        }

        return encoding.Contains("deflate", StringComparison.OrdinalIgnoreCase)
            ? new DeflateStream(request.Body, CompressionMode.Decompress)
            : request.Body;
    }

    private async Task<RunnerWorkspaceGrant> AuthorizeWorkspaceAsync(Guid jobId, int generation, CancellationToken ct)
    {
        var runnerId = RunnerCallerIdentity.RunnerId(this.HttpContext);
        return runnerId is null
            ? RunnerWorkspaceGrant.NotAuthorized(RunnerCallRefusal.NotTheLeaseHolder)
            : await workspace.AuthorizeFetchAsync(
                new RunnerCallContext(jobId, generation, runnerId.Value.ToString("D")),
                ct);
    }

    private IActionResult WorkspaceRefusal(RunnerWorkspaceGrant grant)
    {
        return grant.Refusal == RunnerWorkspaceRefusal.NotAuthorized
            ? this.LeaseRefusal(grant.CallRefusal)
            : this.Conflict(new RunnerContractError("workspace_unavailable", grant.Reason ?? "Unavailable."));
    }

    /// <summary>
    ///     Builds the call context from the authenticated runner and the job the request names. The identity
    ///     comes from the credential; the request only says which job and which generation it believes it
    ///     holds, and the services check both against the job's current state.
    /// </summary>
    private bool TryBuildCall(RunnerJobCallRequest request, out RunnerCallContext call, out IActionResult? failure)
    {
        call = null!;
        var runnerId = RunnerCallerIdentity.RunnerId(this.HttpContext);
        if (runnerId is null)
        {
            failure = this.Unauthorized(new RunnerContractError(RunnerContractError.RegistrationRevoked, "No runner credential was resolved."));
            return false;
        }

        if (!RunnerContractVersion.IsSupported(request.ContractVersion))
        {
            failure = this.StatusCode(
                StatusCodes.Status409Conflict,
                RunnerContractError.ForUnsupportedVersion(request.ContractVersion));
            return false;
        }

        call = new RunnerCallContext(request.JobId, request.LeaseGeneration, runnerId.Value.ToString("D"));
        failure = null;
        return true;
    }

    private IActionResult LeaseRefusal(RunnerCallRefusal refusal)
    {
        return this.Conflict(new RunnerContractError(RunnerContractError.LeaseNotHeld, refusal.ToString()));
    }

    private IActionResult ToolResponse<T>(RunnerToolResult<T> result, T? value)
    {
        return result.Refusal != RunnerCallRefusal.None
            ? this.LeaseRefusal(result.Refusal)
            : this.Ok(new { unavailable = result.Unavailable, value });
    }
}

/// <summary>What every runner call carries: which job, which generation, and which contract version.</summary>
public class RunnerJobCallRequest
{
    /// <summary>The job the call concerns.</summary>
    public Guid JobId { get; init; }

    /// <summary>The lease generation the caller believes it holds.</summary>
    public int LeaseGeneration { get; init; }

    /// <summary>The contract version the caller speaks.</summary>
    public int ContractVersion { get; init; }
}

/// <summary>A knowledge question.</summary>
public sealed class RunnerKnowledgeRequest : RunnerJobCallRequest
{
    /// <summary>The question to ask against configured knowledge sources.</summary>
    public string Question { get; init; } = string.Empty;
}

/// <summary>A linked-item lookup.</summary>
public sealed class RunnerLinkedItemRequest : RunnerJobCallRequest
{
    /// <summary>Provider-native identifier of the linked item.</summary>
    public string ProviderKey { get; init; } = string.Empty;
}

/// <summary>A symbol-insight question for the code-knowledge service.</summary>
public sealed class RunnerSymbolInsightRequest : RunnerJobCallRequest
{
    /// <summary>The symbol to look up.</summary>
    public string Symbol { get; init; } = string.Empty;

    /// <summary>Optional query mode the gateway understands.</summary>
    public string? QueryMode { get; init; }

    /// <summary>Optional ceiling on how many relations to return.</summary>
    public int? MaxRelations { get; init; }
}

/// <summary>A request to resolve a related link on a linked item.</summary>
public sealed class RunnerRelatedItemRequest : RunnerJobCallRequest
{
    /// <summary>Provider-native key of the related target.</summary>
    public string RelatedTargetKey { get; init; } = string.Empty;
}

/// <summary>One file's draft result, to be reconsidered against thread memory.</summary>
public sealed class RunnerMemoryReconsiderRequest : RunnerJobCallRequest
{
    /// <summary>The file whose draft is being reconsidered.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>An excerpt of the change, used to retrieve relevant memory.</summary>
    public string? ChangeExcerpt { get; init; }

    /// <summary>The draft's summary text.</summary>
    public string? DraftSummary { get; init; }

    /// <summary>The draft's comments. Reconsideration may drop or reword them; it never adds findings.</summary>
    public IReadOnlyList<ReviewComment> DraftComments { get; init; } = [];

    /// <summary>The review temperature the reconsideration model runs at, when the job pins one.</summary>
    public float? Temperature { get; init; }
}

/// <summary>A relayed completion.</summary>
public sealed class RunnerChatRequest : RunnerJobCallRequest
{
    /// <summary>The named model role to use. A name, never a connection.</summary>
    public string LogicalModelName { get; init; } = string.Empty;

    /// <summary>Identifies the attempt, so a retry is answered rather than charged again.</summary>
    public string IdempotencyKey { get; init; } = string.Empty;

    /// <summary>The conversation to complete.</summary>
    public IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> Messages { get; init; } = [];

    /// <summary>The portable options for the call: tool declarations, temperature, ceiling, reasoning.</summary>
    public RunnerChatOptions? Options { get; init; }
}

/// <summary>One batch of executor output.</summary>
public sealed class RunnerIngestRequest : RunnerJobCallRequest
{
    /// <summary>Position in this job's stream, starting at 1.</summary>
    public int Sequence { get; init; }

    /// <summary>Identifies the batch, so a resend is recognised.</summary>
    public string IdempotencyKey { get; init; } = string.Empty;

    /// <summary>Trace events, in the order they occurred.</summary>
    public IReadOnlyList<RunnerTraceEvent> Events { get; init; } = [];

    /// <summary>Per-file outcomes finished since the last batch.</summary>
    public IReadOnlyList<RunnerFileOutcome> FileResults { get; init; } = [];

    /// <summary>Spend records for completions made through the relay.</summary>
    public IReadOnlyList<RunnerSpendRecord> Spend { get; init; } = [];
}

/// <summary>One chunk of a findings submission.</summary>
public sealed class RunnerFindingsRequest : RunnerJobCallRequest
{
    /// <summary>Identifies the whole submission across its chunks and retries.</summary>
    public string SubmissionId { get; init; } = string.Empty;

    /// <summary>Zero-based position of this chunk.</summary>
    public int ChunkIndex { get; init; }

    /// <summary>How many chunks the submission has.</summary>
    public int ChunkCount { get; init; }

    /// <summary>The review summary, carried on the final chunk.</summary>
    public string? Summary { get; init; }

    /// <summary>The findings in this chunk.</summary>
    public IReadOnlyList<ReviewComment> Comments { get; init; } = [];

    /// <summary>What the review says about itself, carried on the final chunk.</summary>
    public RunnerResultAnnotations? Annotations { get; init; }
}
