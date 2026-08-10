// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Renders prompt templates from the shipped <c>AI/Prompts</c> tree.
/// </summary>
/// <remarks>
///     Public because the templates are not all review stages: a feature project that ships its own templates
///     into the same tree renders them through <see cref="RenderTemplateFile" /> rather than reimplementing the
///     file lookup, the partial registry and the validation pass.
/// </remarks>
public static class PromptTemplateRuntime
{
    private static readonly Lazy<PromptTemplateFileProvider> FileProvider =
        new(() => new PromptTemplateFileProvider(AppContext.BaseDirectory));

    private static readonly Lazy<PromptTemplatePartialRegistry> PartialRegistry =
        new(() => new PromptTemplatePartialRegistry(FileProvider.Value));

    private static readonly Lazy<HandlebarsPromptRenderer> Renderer = new(() => new HandlebarsPromptRenderer());
    private static readonly Lazy<PromptTemplateValidator> Validator = new(() => new PromptTemplateValidator(FileProvider.Value));

    /// <summary>
    ///     Renders the template at <paramref name="relativePath" /> under the prompt root, with the shared
    ///     partials available to it.
    /// </summary>
    /// <param name="relativePath">Path under <c>AI/Prompts</c>, for example <c>code-insights/finding-type-system.hbs</c>.</param>
    /// <param name="model">The model the template is rendered against.</param>
    public static string RenderTemplateFile(string relativePath, object? model = null)
    {
        try
        {
            var template = FileProvider.Value.ReadTemplate(relativePath);
            var partials = PartialRegistry.Value.GetPartials();
            Validator.Value.ValidateTemplate(relativePath, template, partials);
            return Renderer.Value.Render(template, model, partials).TrimEnd();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Failed to render prompt template '{relativePath}': {ex.Message}", ex);
        }
    }

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
    ///     <paramref name="assertiveCertaintyGate" /> and <paramref name="designReviewScope" /> flags,
    ///     so that <c>{{#if assertiveCertaintyGate}}</c> and <c>{{#if designReviewScope}}</c> blocks
    ///     resolve correctly before the guidance text is embedded into the global system prompt via
    ///     triple-mustache.
    /// </summary>
    /// <summary>
    ///     Renders the <c>output-language</c> shared partial for the given BCP 47 language tag. Every prompt that
    ///     emits reviewer-facing prose appends this one fragment, so all of them state the same rule.
    /// </summary>
    internal static string RenderOutputLanguage(string languageTag)
    {
        try
        {
            var template = FileProvider.Value.ReadSharedPartial("output-language");
            var partials = PartialRegistry.Value.GetPartials();
            return Renderer.Value.Render(template, new { outputLanguage = languageTag }, partials).TrimEnd();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Failed to render output-language partial: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Renders the <c>developer-reply</c> shared partial, which a thread evaluation appends when the thread
    ///     carries a reply nobody has answered yet. It states that the evaluation is being shown a code change
    ///     and a person's words together, and that both are owed an answer.
    /// </summary>
    internal static string RenderDeveloperReply()
    {
        try
        {
            var template = FileProvider.Value.ReadSharedPartial("developer-reply");
            var partials = PartialRegistry.Value.GetPartials();
            return Renderer.Value.Render(template, null, partials).TrimEnd();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Failed to render developer-reply partial: {ex.Message}", ex);
        }
    }

    internal static string RenderAgenticLoopGuidance(bool assertiveCertaintyGate, bool designReviewScope)
    {
        try
        {
            var template = FileProvider.Value.ReadSharedPartial("agentic-loop-guidance");
            var partials = PartialRegistry.Value.GetPartials();
            return Renderer.Value.Render(template, new { assertiveCertaintyGate, designReviewScope }, partials).TrimEnd();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Failed to render agentic-loop-guidance partial: {ex.Message}", ex);
        }
    }
}
