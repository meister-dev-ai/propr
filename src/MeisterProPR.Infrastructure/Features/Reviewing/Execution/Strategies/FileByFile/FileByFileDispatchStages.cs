// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.ProRV.Abstractions;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

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
            !context.FileReviewContext.EnableProRV)
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
