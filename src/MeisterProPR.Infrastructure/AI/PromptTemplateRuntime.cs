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

    /// <summary>
    ///     Renders the <c>agentic-loop-guidance</c> shared partial with the given
    ///     <paramref name="assertiveCertaintyGate" /> flag, so that
    ///     <c>{{#if assertiveCertaintyGate}}</c> blocks resolve correctly before the
    ///     guidance text is embedded into the global system prompt via triple-mustache.
    /// </summary>
    internal static string RenderAgenticLoopGuidance(bool assertiveCertaintyGate)
    {
        try
        {
            var template = FileProvider.Value.ReadSharedPartial("agentic-loop-guidance");
            var partials = PartialRegistry.Value.GetPartials();
            return Renderer.Value.Render(template, new { assertiveCertaintyGate }, partials).TrimEnd();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Failed to render agentic-loop-guidance partial: {ex.Message}", ex);
        }
    }
}
