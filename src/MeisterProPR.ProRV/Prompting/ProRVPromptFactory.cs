// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.ProRV.Knowledge;
using MeisterProPR.ProRV.Models;

namespace MeisterProPR.ProRV.Prompting;

internal sealed class ProRVPromptFactory(IProRVKnowledgeCatalog catalog)
{
    public string BuildApplicabilitySystemPrompt()
    {
        return ProRVPrompts.ApplicabilitySystemPrompt;
    }

    public string BuildApplicabilityUserMessage(
        string language,
        ProRVPrefilterRequest request,
        IReadOnlyList<ProRVCheckDefinition> checks)
    {
        return ProRVPrompts.BuildApplicabilityUserMessage(language, request, checks);
    }

    public string BuildRefinementSystemPrompt()
    {
        return ProRVPrompts.RefinementSystemPrompt;
    }

    public string BuildRefinementUserMessage(
        string language,
        string filePath,
        string unifiedDiff,
        IReadOnlyCollection<string> selectedCheckIds)
    {
        ArgumentNullException.ThrowIfNull(selectedCheckIds);

        var selectedChecks = catalog
            .GetCheckIndex(language)
            .Where(check => selectedCheckIds.Contains(check.Id, StringComparer.Ordinal))
            .Select(check => new ProRVInstructionPrompt(
                check.Id,
                check.Title,
                catalog.GetInstruction(language, check.Id)))
            .ToList()
            .AsReadOnly();

        return ProRVPrompts.BuildRefinementUserMessage(filePath, unifiedDiff, selectedChecks);
    }
}

internal sealed record ProRVInstructionPrompt(string Id, string Title, string Instruction);
