// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.Features.Budgeting.Models;

namespace MeisterProPR.Application.Features.Budgeting;

/// <summary>
///     Publishes a structured budget event when a cap is reached, for a downstream notification/alerting capability
///     to consume. Publishing is fire-and-forget: implementations never throw into the review pipeline.
/// </summary>
public interface IBudgetEventPublisher
{
    /// <summary>Records <paramref name="notification" /> as a consumable budget event. Never throws.</summary>
    Task PublishAsync(BudgetEventNotification notification, CancellationToken ct = default);
}
