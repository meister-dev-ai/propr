// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Features.Budgeting.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Budgeting;

/// <summary>
///     Persists a budget event when a cap is reached. Emission is fire-and-forget: a persistence failure is logged
///     and swallowed so a budget transition never breaks the review it accompanies.
/// </summary>
public sealed partial class BudgetEventPublisher(
    IBudgetEventRepository repository,
    TimeProvider timeProvider,
    ILogger<BudgetEventPublisher> logger) : IBudgetEventPublisher
{
    /// <inheritdoc />
    public async Task PublishAsync(BudgetEventNotification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        try
        {
            var budgetEvent = new BudgetEvent
            {
                Id = Guid.NewGuid(),
                ClientId = notification.ClientId,
                EventType = notification.EventType,
                Scope = notification.Scope,
                ThresholdUsd = notification.ThresholdUsd,
                SpentUsd = notification.SpentUsd,
                JobId = notification.JobId,
                PullRequestId = notification.PullRequestId,
                IterationId = notification.IterationId,
                OccurredAt = timeProvider.GetUtcNow().UtcDateTime,
            };

            await repository.AddAsync(budgetEvent, ct).ConfigureAwait(false);
            LogBudgetEventPublished(logger, notification.ClientId, notification.EventType, notification.Scope);
        }
        catch (Exception ex)
        {
            LogBudgetEventPublishFailed(logger, ex, notification.ClientId, notification.EventType, notification.Scope);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Budget event published for client {ClientId}: {EventType} at {Scope} scope")]
    private static partial void LogBudgetEventPublished(
        ILogger logger,
        Guid clientId,
        Domain.Enums.BudgetEventType eventType,
        Domain.Enums.BudgetScopeKind scope);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to publish budget event for client {ClientId}: {EventType} at {Scope} scope")]
    private static partial void LogBudgetEventPublishFailed(
        ILogger logger,
        Exception exception,
        Guid clientId,
        Domain.Enums.BudgetEventType eventType,
        Domain.Enums.BudgetScopeKind scope);
}
