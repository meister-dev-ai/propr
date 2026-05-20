// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.AI;

internal static class PromptTemplateRuntime
{
    private static readonly Lazy<PromptTemplateFileProvider> FileProvider =
        new(() => new PromptTemplateFileProvider(AppContext.BaseDirectory));

    private static readonly Lazy<PromptTemplatePartialRegistry> PartialRegistry =
        new(() => new PromptTemplatePartialRegistry(FileProvider.Value));

    private static readonly Lazy<HandlebarsPromptRenderer> Renderer = new(() => new HandlebarsPromptRenderer());
    private static readonly Lazy<PromptTemplateValidator> Validator = new(() => new PromptTemplateValidator(FileProvider.Value));

    internal static string RenderStage(string stageKey, object? model = null)
    {
        try
        {
            var template = FileProvider.Value.ReadStageTemplate(stageKey);
            var partials = PartialRegistry.Value.GetPartials();
            Validator.Value.ValidateTemplate(stageKey, template, partials);
            return Renderer.Value.Render(template, model, partials).TrimEnd();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Failed to render prompt stage '{stageKey}': {ex.Message}", ex);
        }
    }

    internal static string ReadSharedPartial(string partialName)
    {
        try
        {
            return FileProvider.Value.ReadSharedPartial(partialName);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Failed to read prompt shared partial '{partialName}': {ex.Message}", ex);
        }
    }
}
