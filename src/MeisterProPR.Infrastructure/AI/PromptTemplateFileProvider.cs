// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.AI;

internal sealed class PromptTemplateFileProvider
{
    internal const string PromptRootRelativePath = "AI/Prompts";

    internal PromptTemplateFileProvider(string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        this.PromptRootPath = Path.Combine(contentRootPath, PromptRootRelativePath);
    }

    internal string PromptRootPath { get; }

    internal string ReadStageTemplate(string stageKey)
    {
        var descriptor = PromptTemplateCatalog.Get(stageKey);
        var path = this.GetAbsolutePath(descriptor.TemplateRelativePath);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Prompt template for stage '{stageKey}' was not found at '{descriptor.TemplateRelativePath}'.");
        }

        return File.ReadAllText(path);
    }

    internal IReadOnlyDictionary<string, string> ReadSharedPartials()
    {
        var partialsPath = this.GetAbsolutePath("shared/partials");
        if (!Directory.Exists(partialsPath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var partials = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var filePath in Directory.GetFiles(partialsPath, "*.hbs", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            partials[name] = File.ReadAllText(filePath);
        }

        return partials;
    }

    internal string ReadSharedPartial(string partialName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partialName);

        var partialPath = this.GetAbsolutePath($"shared/partials/{partialName}.hbs");
        if (!File.Exists(partialPath))
        {
            throw new InvalidOperationException($"Prompt shared partial '{partialName}' was not found at 'shared/partials/{partialName}.hbs'.");
        }

        return File.ReadAllText(partialPath);
    }

    private string GetAbsolutePath(string relativePath)
    {
        return Path.Combine(this.PromptRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
