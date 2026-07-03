// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.RegularExpressions;

namespace MeisterProPR.Infrastructure.AI;

internal sealed class PromptTemplateValidator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex PartialReferencePattern = new(@"{{>\s*(?<name>[^\s}]+)", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex ValidPartialNamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9_-]*$", RegexOptions.Compiled, RegexTimeout);

    private readonly PromptTemplateFileProvider _fileProvider;

    internal PromptTemplateValidator(PromptTemplateFileProvider fileProvider)
    {
        this._fileProvider = fileProvider ?? throw new ArgumentNullException(nameof(fileProvider));
    }

    internal void ValidateStageTemplate(string stageKey)
    {
        var template = this._fileProvider.ReadStageTemplate(stageKey);
        this.ValidateTemplate(stageKey, template, this._fileProvider.ReadSharedPartials());
    }

    internal void ValidateTemplate(string stageKey, string template, IReadOnlyDictionary<string, string> partials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        ArgumentNullException.ThrowIfNull(partials);

        ValidatePartialReferences($"stage '{stageKey}'", template, partials, new HashSet<string>(StringComparer.Ordinal));
    }

    internal static void ValidatePartialName(string partialName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partialName);

        if (!ValidPartialNamePattern.IsMatch(partialName))
        {
            throw new InvalidOperationException(
                $"Prompt partial reference '{partialName}' is invalid. Only letters, numbers, underscores, and hyphens are allowed.");
        }
    }

    private static void ValidatePartialReferences(
        string owner,
        string template,
        IReadOnlyDictionary<string, string> partials,
        ISet<string> visitedPartials)
    {
        foreach (Match match in PartialReferencePattern.Matches(template))
        {
            var partialName = match.Groups["name"].Value;
            ValidatePartialName(partialName);

            if (!partials.TryGetValue(partialName, out var partialTemplate))
            {
                throw new InvalidOperationException($"Prompt {owner} references missing shared partial '{partialName}'.");
            }

            if (!visitedPartials.Add(partialName))
            {
                continue;
            }

            ValidatePartialReferences($"partial '{partialName}'", partialTemplate, partials, visitedPartials);
        }
    }
}
