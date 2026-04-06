// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;

/// <summary>
///     Reviewing-owned boundary for recording protocol events during review execution.
/// </summary>
public interface IReviewProtocolRecorder : IProtocolRecorder
{
}
