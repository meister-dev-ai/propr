// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using System.Xml;
using System.Xml.Linq;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>Builds and recognizes the canonical single-line HTML assessment section.</summary>
internal static class HtmlFindingAssessmentFormatter
{
    private const string Prefix = "<details><summary>Assessment details</summary><table><tr><th>Assessment</th><th>Level</th><th>Reason</th></tr>";
    private const string Suffix = "</table></details>";
    private static readonly MarkdownPipeline CellPipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    internal static string Format(FindingRiskAssessment risk)
    {
        var riskReason = RiskReason(ReviewFindingTextFormatter.FormatLevel(risk.Severity));
        return ReviewPresentationTemplateCatalog.Shared.Render(
            ReviewPresentationTemplate.HtmlAssessment,
            new ReviewPresentationModels.Assessment(
            [
                new("Risk", ReviewFindingTextFormatter.FormatLevel(risk.Severity), RenderCell(riskReason)),
                new("Impact", ReviewFindingTextFormatter.FormatLevel(risk.Impact), RenderCell(risk.ImpactReason ?? "Not assessed.")),
                new(
                    "Likelihood",
                    risk.Likelihood == FindingLikelihood.NotApplicable ? "Not applicable" : ReviewFindingTextFormatter.FormatLevel(risk.Likelihood),
                    RenderCell(risk.LikelihoodReason ?? "Not assessed.")),
                new("Confidence", ReviewFindingTextFormatter.FormatLevel(risk.Confidence), RenderCell(risk.ConfidenceReason ?? "Not assessed.")),
            ]));
    }

    internal static bool TryReadLevels(string assessment, out string[] levels)
    {
        levels = new string[4];
        if (!assessment.StartsWith(Prefix, StringComparison.Ordinal) || assessment.IndexOfAny(['\r', '\n']) >= 0)
        {
            return false;
        }

        var remaining = assessment[Prefix.Length..];
        var dimensions = new[] { "Risk", "Impact", "Likelihood", "Confidence" };
        for (var index = 0; index < dimensions.Length; index++)
        {
            var rowPrefix = $"<tr><td>{dimensions[index]}</td><td>";
            if (!remaining.StartsWith(rowPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var levelEnd = remaining.IndexOf("</td><td>", rowPrefix.Length, StringComparison.Ordinal);
            var reasonEnd = levelEnd < 0 ? -1 : remaining.IndexOf("</td></tr>", levelEnd + 9, StringComparison.Ordinal);
            if (reasonEnd < 0)
            {
                return false;
            }

            var level = remaining[rowPrefix.Length..levelEnd];
            var reason = remaining[(levelEnd + 9)..reasonEnd];
            var known = index switch
            {
                0 => ReviewFindingTextFormatter.IsLevel<FindingRiskSeverity>(level),
                1 => ReviewFindingTextFormatter.IsLevel<FindingImpact>(level),
                2 => level == "Not applicable" || ReviewFindingTextFormatter.IsLevel<FindingLikelihood>(level),
                _ => ReviewFindingTextFormatter.IsLevel<FindingConfidence>(level),
            };
            if (!known || (index == 0 ? reason != RiskReason(level) : !IsGeneratedCell(reason)))
            {
                return false;
            }

            levels[index] = level;
            remaining = remaining[(reasonEnd + 10)..];
        }

        return remaining == Suffix;
    }

    private static string RiskReason(string level) => level == "Unassessed"
        ? "No scored impact and likelihood assessment is available."
        : "Derived from impact and likelihood.";

    private static string RenderCell(string source)
    {
        source = source.ReplaceLineEndings(" ");
        var document = Markdown.Parse(source, CellPipeline);
        if (document.Count != 1 || document[0] is not ParagraphBlock { Inline: { } inline })
        {
            return WebUtility.HtmlEncode(source);
        }

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer) { LinkRewriter = url => IsSafeUrl(url) ? url : "#" };
        CellPipeline.Setup(renderer);
        renderer.Render(inline);
        return writer.ToString().ReplaceLineEndings(" ");
    }

    private static bool IsSafeUrl(string url) => !url.Any(char.IsControl)
                                                 && (url.StartsWith('#') || (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                                                                             uri.Scheme is "http" or "https" or "mailto"));

    // This validates generated suffixes for identity matching. It never renders supplied HTML.
    private static bool IsGeneratedCell(string fragment)
    {
        if (fragment.Length == 0)
        {
            return false;
        }

        try
        {
            using var reader = XmlReader.Create(
                new StringReader("<cell>" + fragment + "</cell>"), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 100000,
                });
            return XElement.Load(reader, LoadOptions.PreserveWhitespace).Nodes().All(IsGeneratedNode);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool IsGeneratedNode(XNode node)
    {
        if (node.GetType() == typeof(XText))
        {
            return true;
        }

        if (node is not XElement element || element.Name.NamespaceName.Length != 0)
        {
            return false;
        }

        var attributes = element.Attributes().ToArray();
        var valid = element.Name.LocalName switch
        {
            "code" or "em" or "strong" or "br" => attributes.Length == 0,
            "a" => attributes.All(attribute => attribute.Name == "title" || (attribute.Name == "href" && IsSafeUrl(attribute.Value)))
                   && element.Attribute("href") is not null,
            "img" => attributes.All(attribute =>
                         attribute.Name == "alt" || attribute.Name == "title" || (attribute.Name == "src" && IsSafeUrl(attribute.Value)))
                     && element.Attribute("src") is not null,
            _ => false,
        };
        return valid && element.Nodes().All(IsGeneratedNode);
    }
}
