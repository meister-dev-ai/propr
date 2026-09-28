// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;

/// <summary>
///     Answers a job's reasoning-capture question from the tenant that owns its client, falling back to the
///     installation switch.
/// </summary>
/// <remarks>
///     A job whose row cannot be read, or whose row names no client, answers "do not capture". No tenant has
///     authorised the text in that case, and an invalid, stale or half-written job is not a reason to store
///     reasoning on the installation's behalf. The refusal is logged, because a job that keeps answering this
///     way is a persistence fault and not a policy.
/// </remarks>
public sealed partial class RunnerJobReasoningCapturePolicy(
    IJobRepository jobs,
    AiReviewOptions reviewOptions,
    ITenantReasoningCapturePolicyProvider? tenantPolicies = null,
    ILogger<RunnerJobReasoningCapturePolicy>? logger = null) : IRunnerJobReasoningCapturePolicy
{
    /// <inheritdoc />
    public async Task<bool> CapturesReasoningAsync(Guid jobId, CancellationToken ct = default)
    {
        if (tenantPolicies is null)
        {
            // No tenant surface is composed, so the installation switch decides.
            return reviewOptions.CaptureReasoningInProtocol;
        }

        Guid? clientId;
        try
        {
            clientId = jobs.GetById(jobId)?.ClientId;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The store answered with a fault instead of a row, so no tenant has permitted the text. The
            // remark on this type promises "do not capture" for a job that cannot be read, and a fault is one
            // of the ways it cannot be read.
            if (logger is not null)
            {
                LogJobLookupFailed(logger, jobId, exception);
            }

            return false;
        }

        if (clientId is null || clientId.Value == Guid.Empty)
        {
            if (logger is not null)
            {
                LogJobOwnerUnresolved(logger, jobId);
            }

            return false;
        }

        var stated = (await tenantPolicies.GetForClientAsync(clientId.Value, ct)).AsOverride();

        return stated ?? reviewOptions.CaptureReasoningInProtocol;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Review job {JobId} could not be resolved to the client that owns it. Its reasoning is not recorded, because no tenant has permitted it.")]
    private static partial void LogJobOwnerUnresolved(ILogger logger, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Review job {JobId} could not be read. Its reasoning is not recorded, because no tenant has permitted it.")]
    private static partial void LogJobLookupFailed(ILogger logger, Guid jobId, Exception exception);
}
