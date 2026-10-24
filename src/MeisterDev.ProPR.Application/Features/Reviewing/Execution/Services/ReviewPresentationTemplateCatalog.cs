// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using HandlebarsDotNet;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

internal enum ReviewPresentationTemplate
{
    Finding,
    MarkdownAssessment,
    HtmlAssessment,
    Summary,
    SummaryAction,
    SummaryDetails,
}

/// <summary>Prepares the finite embedded presentation templates once for the lifetime of the process.</summary>
internal sealed class ReviewPresentationTemplateCatalog
{
    private const string ResourcePrefix = "MeisterDev.ProPR.Application.ReviewPresentation.";
    private readonly object _compilationLock = new();
    private readonly IReadOnlyDictionary<ReviewPresentationTemplate, Lazy<Func<object, string>>> _templates;

    internal static ReviewPresentationTemplateCatalog Shared { get; } = new();

    internal ReviewPresentationTemplateCatalog(
        Func<ReviewPresentationTemplate, string>? loadTemplate = null,
        Func<string, Func<object, string>>? compileTemplate = null)
    {
        loadTemplate ??= ReadEmbeddedTemplate;
        // Values are prepared Markdown or HTML fragments. Escaping here would change source and replacement bytes.
        var environment = Handlebars.Create(new HandlebarsConfiguration { NoEscape = true, FormatProvider = CultureInfo.InvariantCulture });
        compileTemplate ??= text =>
        {
            var compiled = environment.Compile(text);
            return model => compiled(model);
        };
        this._templates = Enum.GetValues<ReviewPresentationTemplate>().ToDictionary(
            template => template,
            template => new Lazy<Func<object, string>>(
                () => this.Prepare(template, loadTemplate, compileTemplate), LazyThreadSafetyMode.ExecutionAndPublication));
    }

    internal string Render(ReviewPresentationTemplate template, object model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!this._templates.TryGetValue(template, out var compiled))
        {
            throw new ArgumentOutOfRangeException(nameof(template), template, "Unknown presentation template.");
        }

        return compiled.Value(model);
    }

    private Func<object, string> Prepare(
        ReviewPresentationTemplate template,
        Func<ReviewPresentationTemplate, string> loadTemplate,
        Func<string, Func<object, string>> compileTemplate)
    {
        var text = loadTemplate(template);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"Presentation template '{template}' is empty.");
        }

        // Different templates can be requested concurrently on first use; compilation shares one environment and is serialized.
        lock (this._compilationLock)
        {
            try
            {
                return compileTemplate(text);
            }
            catch (Exception exception) when (exception is HandlebarsCompilerException or HandlebarsRuntimeException or InvalidOperationException)
            {
                throw new InvalidOperationException($"Failed to compile presentation template '{template}'.", exception);
            }
        }
    }

    private static string ReadEmbeddedTemplate(ReviewPresentationTemplate template)
    {
        var fileName = template switch
        {
            ReviewPresentationTemplate.Finding => "finding",
            ReviewPresentationTemplate.MarkdownAssessment => "assessment-markdown",
            ReviewPresentationTemplate.HtmlAssessment => "assessment-html",
            ReviewPresentationTemplate.Summary => "summary",
            ReviewPresentationTemplate.SummaryAction => "summary-action",
            ReviewPresentationTemplate.SummaryDetails => "summary-details",
            _ => throw new ArgumentOutOfRangeException(nameof(template)),
        };
        using var stream = typeof(ReviewPresentationTemplateCatalog).Assembly.GetManifestResourceStream(ResourcePrefix + fileName + ".hbs")
                           ?? throw new InvalidOperationException($"Embedded presentation template '{template}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
