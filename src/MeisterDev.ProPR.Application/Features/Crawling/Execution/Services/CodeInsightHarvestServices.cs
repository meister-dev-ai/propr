// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Contracts;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

/// <summary>A complete optional harvest binding; licensing remains a collection-gate decision.</summary>
internal sealed record CodeInsightHarvestServices(ICodeInsightMissHarvester Harvester, ICodeInsightHarvestCoverageRecorder Coverage)
{
    internal static CodeInsightHarvestServices? Bind(ICodeInsightMissHarvester? harvester, ICodeInsightHarvestCoverageRecorder? coverage)
    {
        return harvester is not null && coverage is not null ? new(harvester, coverage) : null;
    }
}
