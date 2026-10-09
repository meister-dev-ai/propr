// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>Determines human-thread eligibility from providerless retained discussion evidence.</summary>
public static class HarvestedThreadEligibility
{
    public const string SummaryPrefix = HistoricalPublicationEvidenceDecoder.SummaryPrefix;
    public const string DefaultMarkerWording = HistoricalPublicationEvidenceDecoder.DefaultMarkerWording;

    public static bool IsHumanThread(string? discussion, string? generatedMarker = null)
    {
        if (string.IsNullOrWhiteSpace(discussion))
        {
            return false;
        }

        var lines = discussion.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sawHumanLine = false;
        foreach (var line in lines)
        {
            var evidence = HistoricalPublicationEvidenceDecoder.DecodeLine(line, generatedMarker);
            if (evidence.IsOwnPublication)
            {
                return false;
            }

            if (!evidence.IsProviderActivity)
            {
                sawHumanLine = true;
            }
        }

        return sawHumanLine;
    }
}
