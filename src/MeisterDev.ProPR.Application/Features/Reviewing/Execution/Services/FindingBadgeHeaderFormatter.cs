// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>Builds and recognizes canonical finding badge headers.</summary>
internal static class FindingBadgeHeaderFormatter
{
    private const string SeverityDimension = "Severity";
    private const string TypeDimension = "Type";
    private const string NotApplicable = "Not applicable";
    private const int MaximumHistoryMetadataLength = 1024;
    private const int MaximumLabelScalars = 128;
    private const string MarkdownEscapes = "\\`*_{}[]()#+.!|<>-";

    private static readonly string[] Severities = ["Error", "Warning", "Suggestion", "Info"];

    // The assessment badges of an assessed finding, in the order in which they follow the severity badge.
    private static readonly AssessmentDimension[] Assessment =
    [
        new("Risk", risk => ReviewFindingTextFormatter.FormatLevel(risk.Severity), ReviewFindingTextFormatter.IsLevel<FindingRiskSeverity>),
        new("Impact", risk => ReviewFindingTextFormatter.FormatLevel(risk.Impact), ReviewFindingTextFormatter.IsLevel<FindingImpact>),
        new(
            "Likelihood",
            risk => risk.Likelihood == FindingLikelihood.NotApplicable ? NotApplicable : ReviewFindingTextFormatter.FormatLevel(risk.Likelihood),
            value => value == NotApplicable || ReviewFindingTextFormatter.IsLevel<FindingLikelihood>(value)),
        new("Confidence", risk => ReviewFindingTextFormatter.FormatLevel(risk.Confidence), ReviewFindingTextFormatter.IsLevel<FindingConfidence>),
    ];

    private static readonly Regex Image = new(
        @"^!\[((?:\\.|[^\]\\])*)\]\((https://img\.shields\.io/badge/[^\s()]*)\)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal sealed record Badge(string Dimension, string Value);

    /// <summary>A recognized header. <see cref="Levels" /> holds the assessment levels in table order, or null without an assessment.</summary>
    internal sealed record Header(string Severity, string[]? Levels, FindingBadgeStyle Style, IReadOnlyList<Badge> Badges)
    {
        internal IEnumerable<Badge> Metadata => this.Badges.Where(badge => badge.Dimension != TypeDimension);

        internal IReadOnlyList<Badge> Types => [.. this.Badges.Where(badge => badge.Dimension == TypeDimension)];

        /// <summary>The badges as plain text for review memory, with the finding types bounded to the history limit.</summary>
        internal string CompactText
        {
            get
            {
                var text = new StringBuilder(string.Join(" · ", this.Metadata.Select(badge => $"{badge.Dimension}: {badge.Value}")));
                var types = this.Types;
                var included = 0;
                while (included < types.Count)
                {
                    var next = $" · {TypeDimension}: {types[included].Value}";
                    var remaining = types.Count - included - 1;
                    if (text.Length + next.Length + (remaining > 0 ? Omission(remaining).Length : 0) > MaximumHistoryMetadataLength)
                    {
                        break;
                    }

                    text.Append(next);
                    included++;
                }

                return included < types.Count ? text.Append(Omission(types.Count - included)).ToString() : text.ToString();
            }
        }

        private static string Omission(int count) => " · " + count.ToString(CultureInfo.InvariantCulture) + " additional types omitted";
    }

    internal static string Format(string severity, FindingRiskAssessment? risk, IReadOnlyList<string> types, FindingBadgeStyle style)
    {
        IEnumerable<Badge> assessment = risk is null ? [] : Assessment.Select(dimension => new Badge(dimension.Name, dimension.Format(risk)));
        var typeBadges = types.Select(Normalize).Where(label => label.Length > 0).Select(label => new Badge(TypeDimension, label));
        var badges = new[] { new Badge(SeverityDimension, Normalize(severity)) }.Concat(assessment).Concat(typeBadges);
        return string.Join(" ", badges.Select(badge => Render(badge, style)));
    }

    internal static bool TryRead(string text, out Header header)
    {
        header = null!;
        if (text.IndexOfAny(['\r', '\n']) >= 0 || !TryReadBadges(text, out var style, out var badges)
                                               || badges.Count == 0 || badges[0].Dimension != SeverityDimension
                                               || !Severities.Contains(badges[0].Value))
        {
            return false;
        }

        var assessed = badges.Count > 1 && badges[1].Dimension == Assessment[0].Name;
        Badge[] levels = assessed ? [.. badges.Skip(1).Take(Assessment.Length)] : [];
        if (assessed && (levels.Length < Assessment.Length
                         || !levels.Zip(Assessment).All(pair => pair.First.Dimension == pair.Second.Name && pair.Second.IsValid(pair.First.Value))))
        {
            return false;
        }

        if (badges.Skip(1 + levels.Length).Any(badge => badge.Dimension != TypeDimension))
        {
            return false;
        }

        string[]? levelValues = assessed ? [.. levels.Select(badge => badge.Value)] : null;
        header = new(badges[0].Value, levelValues, style, badges);
        return true;
    }

    internal static string Limit(string text, int maximumLength)
    {
        if (text.Length <= maximumLength)
        {
            return text;
        }

        if (!TryRead(text, out var header))
        {
            return string.Empty;
        }

        var limited = new StringBuilder(string.Join(" ", header.Metadata.Select(badge => Render(badge, header.Style))));
        if (limited.Length > maximumLength)
        {
            return string.Empty;
        }

        foreach (var type in header.Types.Select(badge => Render(badge, header.Style)))
        {
            if (limited.Length + 1 + type.Length > maximumLength)
            {
                break;
            }

            limited.Append(' ').Append(type);
        }

        return limited.ToString();
    }

    // Reads badges that are separated by exactly one space. The style of the first badge applies to all of them.
    private static bool TryReadBadges(string text, out FindingBadgeStyle style, out List<Badge> badges)
    {
        style = text.StartsWith("![", StringComparison.Ordinal) ? FindingBadgeStyle.ShieldsIo : FindingBadgeStyle.Plain;
        badges = [];
        var position = 0;
        while (position < text.Length)
        {
            if (!TryTakeBadge(text.AsSpan(position), style, out var badge, out var length))
            {
                return false;
            }

            badges.Add(badge);
            position += length;
            if (position < text.Length)
            {
                if (text[position] != ' ' || position == text.Length - 1)
                {
                    return false;
                }

                position++;
            }
        }

        return true;
    }

    // Recognizes only the canonical rendering of a badge, so an edited header stays part of the comment text.
    private static bool TryTakeBadge(ReadOnlySpan<char> text, FindingBadgeStyle style, out Badge badge, out int length)
    {
        badge = null!;
        var content = style == FindingBadgeStyle.ShieldsIo ? TakeImageText(text, out length) : TakeCodeText(text, out length);
        var separator = content?.IndexOf(": ", StringComparison.Ordinal) ?? -1;
        if (separator < 1)
        {
            return false;
        }

        badge = new(content![..separator], content[(separator + 2)..]);
        return badge.Value.Length > 0 && Normalize(badge.Value) == badge.Value && text[..length].SequenceEqual(Render(badge, style));
    }

    private static string? TakeImageText(ReadOnlySpan<char> text, out int length)
    {
        var matches = Image.EnumerateMatches(text);
        if (!matches.MoveNext())
        {
            length = 0;
            return null;
        }

        length = matches.Current.Length;
        var closing = 2;
        while (closing < length && text[closing] != ']')
        {
            closing += text[closing] == '\\' ? 2 : 1;
        }

        return Unescape(text[2..closing]);
    }

    private static string? TakeCodeText(ReadOnlySpan<char> text, out int length)
    {
        length = 0;
        var opening = ReviewFindingTextFormatter.BacktickRunLength(text, 0);
        if (opening == 0)
        {
            return null;
        }

        for (var index = opening; index < text.Length && length == 0;)
        {
            var run = ReviewFindingTextFormatter.BacktickRunLength(text, index);
            if (run == opening)
            {
                length = index + run;
            }

            index += Math.Max(1, run);
        }

        if (length == 0)
        {
            return null;
        }

        var content = text[opening..(length - opening)].ToString();
        return content.StartsWith(' ') && content.EndsWith(' ') && content.Any(character => character != ' ') ? content[1..^1] : content;
    }

    private static string Render(Badge badge, FindingBadgeStyle style)
    {
        var content = $"{badge.Dimension}: {badge.Value}";
        if (style == FindingBadgeStyle.Plain)
        {
            var longest = 0;
            for (var index = 0; index < content.Length;)
            {
                var run = ReviewFindingTextFormatter.BacktickRunLength(content, index);
                longest = Math.Max(longest, run);
                index += Math.Max(1, run);
            }

            var delimiter = new string('`', longest + 1);
            var padding = content.StartsWith('`') || content.EndsWith('`') ? " " : string.Empty;
            return delimiter + padding + content + padding + delimiter;
        }

        var color = badge.Dimension switch
        {
            TypeDimension => "2563eb",
            "Risk" => badge.Value switch
            {
                "Low" => "2e7d32",
                "Medium" => "ca8a04",
                "High" => "ea580c",
                "Critical" => "dc2626",
                _ => "64748b",
            },
            _ => "64748b",
        };
        var alt = new StringBuilder(content.Length);
        foreach (var character in content)
        {
            if (MarkdownEscapes.Contains(character))
            {
                alt.Append('\\');
            }

            alt.Append(character);
        }

        return $"![{alt}](https://img.shields.io/badge/{Encode(badge.Dimension)}-{Encode(badge.Value)}-{color}?style=flat-square)";
    }

    private static string Encode(string value) =>
        Uri.EscapeDataString(value.Replace("-", "--", StringComparison.Ordinal).Replace("_", "__", StringComparison.Ordinal));

    // Collapses whitespace, removes control characters and keeps at most MaximumLabelScalars Unicode scalars.
    private static string Normalize(string value)
    {
        var normalized = new StringBuilder(Math.Min(value.Length, 256));
        var scalars = 0;
        var pendingSpace = false;
        Span<char> encoded = stackalloc char[2];
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }

            if (Rune.IsControl(rune))
            {
                continue;
            }

            if (pendingSpace && scalars < MaximumLabelScalars)
            {
                normalized.Append(' ');
                scalars++;
            }

            if (scalars == MaximumLabelScalars)
            {
                break;
            }

            normalized.Append(encoded[..rune.EncodeToUtf16(encoded)]);
            scalars++;
            pendingSpace = false;
        }

        return normalized.ToString().TrimEnd();
    }

    private static string Unescape(ReadOnlySpan<char> text)
    {
        var result = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                index++;
            }

            result.Append(text[index]);
        }

        return result.ToString();
    }

    private sealed record AssessmentDimension(string Name, Func<FindingRiskAssessment, string> Format, Func<string, bool> IsValid);
}
