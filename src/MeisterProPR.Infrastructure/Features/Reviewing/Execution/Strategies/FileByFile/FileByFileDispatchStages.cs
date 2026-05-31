// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Text.RegularExpressions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.ProRV.Abstractions;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

internal sealed class FileByFileContextPrefetchStage(AiReviewOptions? options = null, IProtocolRecorder? protocolRecorder = null)
    : IReviewPipelineStage<PerFileReviewContext>
{
    public const string StageIdConstant = "file-by-file.context-prefetch";

    private readonly AiReviewOptions _options = options ?? new AiReviewOptions();
    private readonly IProtocolRecorder? _protocolRecorder = protocolRecorder;

    public string StageId => StageIdConstant;

    public async Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        var fileHint = context.FileReviewContext.PerFileHint;
        var reviewTools = context.FileReviewContext.ReviewTools;
        if (fileHint is null || reviewTools is null)
        {
            return context;
        }

        var evidence = new List<PrefetchedContextEvidenceItem>();
        var surroundingContent = TrimToBudget(
            context.ChangedFile.FullContent, this._options.MaxPrefetchRegionChars,
            out var surroundingTruncated);

        if (!string.IsNullOrWhiteSpace(surroundingContent))
        {
            evidence.Add(
                new PrefetchedContextEvidenceItem(
                    "surrounding_definition",
                    "Changed definition context",
                    context.ChangedFile.Path,
                    surroundingContent,
                    surroundingTruncated));
        }

        var callerBudget = Math.Max(0, this._options.MaxPrefetchCallerSites);
        var searchResult = callerBudget == 0
            ? null
            : await reviewTools.SearchCodeAsync(
                new CodeSearchRequest(
                    context.ChangedFile.Path,
                    CodeSearchModes.RelatedSymbol,
                    RepositorySearchBranchSides.Source,
                    RepositorySearchPathScopes.Repository),
                cancellationToken);

        if (searchResult is { Status: RepositorySearchStatuses.Success or RepositorySearchStatuses.Partial })
        {
            foreach (var match in searchResult.Matches
                         .Where(match => !string.Equals(match.FilePath, context.ChangedFile.Path, StringComparison.Ordinal))
                         .Take(callerBudget))
            {
                var matchText = TrimToBudget(match.MatchText, this._options.MaxPrefetchRegionChars, out var matchTruncated);
                if (string.IsNullOrWhiteSpace(matchText))
                {
                    continue;
                }

                evidence.Add(
                    new PrefetchedContextEvidenceItem(
                        "supported_caller_site",
                        $"Related caller site: {match.FilePath}",
                        match.LineNumber.HasValue ? $"{match.FilePath}:L{match.LineNumber.Value}" : match.FilePath,
                        matchText,
                        matchTruncated || match.Truncated));
            }
        }

        if (evidence.Count == 0)
        {
            return context;
        }

        context.FileReviewContext.PerFileHint = fileHint with
        {
            PrefetchedContextEvidence = evidence,
        };

        var recorder = this._protocolRecorder ?? context.FileReviewContext.ProtocolRecorder;
        if (context.ProtocolId.HasValue && recorder is not null)
        {
            await recorder.RecordReviewStrategyEventAsync(
                context.ProtocolId.Value,
                ReviewProtocolEventNames.ContextPrefetchApplied,
                JsonSerializer.Serialize(
                    new
                    {
                        filePath = context.ChangedFile.Path,
                        evidenceCount = evidence.Count,
                        callerSiteCount = evidence.Count(item => string.Equals(item.Kind, "supported_caller_site", StringComparison.Ordinal)),
                    }),
                JsonSerializer.Serialize(
                    evidence.Select(item => new
                    {
                        item.Kind,
                        item.Title,
                        item.SourceId,
                        item.Truncated,
                    })),
                null,
                cancellationToken);
        }

        return context;
    }

    private static string TrimToBudget(string? content, int maxChars, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var normalized = content.Trim();
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        truncated = true;
        return normalized[..maxChars].TrimEnd();
    }
}

internal sealed class FileByFileRiskMarkerStage : IReviewPipelineStage<PerFileReviewContext>
{
    public const string StageIdConstant = "file-by-file.risk-marker";

    private static readonly IReadOnlyList<(string MarkerId, string Pattern, bool IsSecurity)> MarkerRules =
    [
        ("security.auth-token", "token|oauth|jwt|bearer|secret|apikey|api_key|password|cookie", true),
        ("security.url-redirect", "redirect|returnurl|callbackurl|open\\(|window\\.open|location\\.|iframe|x-frame-options|frame-ancestors|origin|referer",
            true),
        ("security.allow-deny", "allowlist|denylist|whitelist|blacklist|regex.*domain|domain.*regex|cors", true),
        ("concurrency.async-loop", "foreach\\s*\\(\\s*async|promise\\.all|task\\.whenall|await foreach|goroutine|go\\s+func", false),
        ("concurrency.locking", "lock\\s*\\(|semaphore|mutex|monitor\\.|interlocked", false),
        ("concurrency.shared-counter", "\\+\\+|--|updatemany|cache.?key", false),
    ];

    public string StageId => StageIdConstant;

    public Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        if (context.FileReviewContext.PerFileHint is null)
        {
            return Task.FromResult(context);
        }

        var diff = context.ChangedFile.UnifiedDiff;
        if (string.IsNullOrWhiteSpace(diff))
        {
            return Task.FromResult(context);
        }

        var matchedMarkers = MarkerRules
            .Where(rule => Regex.IsMatch(diff, rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Select(rule => (rule.MarkerId, rule.IsSecurity))
            .ToList();

        if (matchedMarkers.Count == 0)
        {
            context.FileReviewContext.PerFileHint = context.FileReviewContext.PerFileHint with
            {
                RiskMarkers = FileRiskMarkers.None,
            };
            return Task.FromResult(context);
        }

        context.FileReviewContext.PerFileHint = context.FileReviewContext.PerFileHint with
        {
            RiskMarkers = new FileRiskMarkers(
                matchedMarkers.Any(marker => marker.IsSecurity),
                matchedMarkers.Any(marker => !marker.IsSecurity),
                matchedMarkers.Select(marker => marker.MarkerId).Distinct(StringComparer.Ordinal).ToArray()),
        };

        return Task.FromResult(context);
    }
}

internal sealed class FileByFileProRvPrefilterStage(
    IProtocolRecorder protocolRecorder,
    IProRVPrefilter? proRvPrefilter,
    IAiConnectionRepository? aiConnectionRepository,
    IAiChatClientFactory? aiClientFactory,
    IAiRuntimeResolver? aiRuntimeResolver,
    ILogger<FileByFileProRvPrefilterStage> logger) : IReviewPipelineStage<PerFileReviewContext>
{
    // This id is part of persisted/profile-selected Reviewing protocol identity.
    public const string StageIdConstant = "file-by-file.prorv-prefilter";

    public string StageId => StageIdConstant;

    public async Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        if (context.ReviewResult is not null ||
            context.FileReviewContext.PerFileHint is null ||
            !context.FileReviewContext.EnableProRV ||
            (context.FileReviewContext.AugmentationMode != ReviewAugmentationMode.EarlySteering
             && context.FileReviewContext.PassKind != ReviewPassKind.ProRVAugmentation))
        {
            return context;
        }

        var fallbackChatClient = context.FileReviewContext.TierChatClient ?? context.FileReviewContext.DefaultReviewChatClient;
        if (fallbackChatClient is null)
        {
            return context;
        }

        var focusedReviewGuidance = await ProRVFocusedReviewGuidanceResolver.TryResolveAsync(
            context.Job,
            context.ChangedFile,
            context.FileReviewContext,
            fallbackChatClient,
            context.ProtocolId,
            protocolRecorder,
            proRvPrefilter,
            aiConnectionRepository,
            aiClientFactory,
            aiRuntimeResolver,
            logger,
            StageIdConstant,
            cancellationToken);

        if (focusedReviewGuidance.Guidance.Count == 0)
        {
            return context;
        }

        context.FileReviewContext.PerFileHint = context.FileReviewContext.PerFileHint with
        {
            FocusedReviewGuidance = focusedReviewGuidance.Guidance,
        };

        return context;
    }
}
