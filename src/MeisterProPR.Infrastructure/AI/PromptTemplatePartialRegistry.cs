// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.AI;

internal sealed class PromptTemplatePartialRegistry
{
    private readonly PromptTemplateFileProvider _fileProvider;

    internal PromptTemplatePartialRegistry(PromptTemplateFileProvider fileProvider)
    {
        this._fileProvider = fileProvider ?? throw new ArgumentNullException(nameof(fileProvider));
    }

    internal IReadOnlyDictionary<string, string> GetPartials()
    {
        var partials = this._fileProvider.ReadSharedPartials();
        foreach (var partial in partials)
        {
            PromptTemplateValidator.ValidatePartialName(partial.Key);

            if (string.IsNullOrWhiteSpace(partial.Value))
            {
                throw new InvalidOperationException($"Prompt shared partial '{partial.Key}' is empty.");
            }
        }

        return partials;
    }
}
