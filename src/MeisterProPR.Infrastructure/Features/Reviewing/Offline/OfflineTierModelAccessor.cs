// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Scoped holder for the per-purpose model selection used by the offline runtime resolver.
/// </summary>
public sealed class OfflineTierModelAccessor : IOfflineTierModelAccessor
{
    /// <inheritdoc />
    public OfflineTierModelSelection? Selection { get; set; }
}
