// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Application.Features.Reviewing.ThreadMemory.Ports;

/// <summary>
///     Reviewing-owned persistence boundary for thread-memory records.
/// </summary>
public interface IReviewThreadMemoryStore : IThreadMemoryRepository
{
}
