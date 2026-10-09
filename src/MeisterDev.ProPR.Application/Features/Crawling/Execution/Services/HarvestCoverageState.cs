// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

/// <summary>Collection completeness and human-thread resolution for one provider enumeration.</summary>
internal sealed class HarvestCoverageState
{
    internal bool AllHumanThreadsResolved { get; private set; } = true;
    internal bool AllHumanObservationsRetained { get; private set; } = true;

    internal void Observe(ThreadUpdatedEvent observation, ThreadResolutionIntent intent)
    {
        if (ThreadUpdatedEventFactory.IsHumanThread(observation)
            && !ThreadResolutionStatusInterpreter.IsResolved(intent))
        {
            this.AllHumanThreadsResolved = false;
        }
    }

    internal void RecordRetention(bool retained)
    {
        this.AllHumanObservationsRetained &= retained;
    }
}
