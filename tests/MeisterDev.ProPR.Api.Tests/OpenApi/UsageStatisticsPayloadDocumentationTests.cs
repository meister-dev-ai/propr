// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Support;

namespace MeisterDev.ProPR.Api.Tests.OpenApi;

/// <summary>
///     The published payload documentation and the type that defines the wire payload must not drift apart.
///     <para>
///         A field that reaches the wire without an entry in the documentation makes the published page
///         inaccurate. These cases compare the two in both directions and fail the build on any difference.
///     </para>
/// </summary>
public sealed class UsageStatisticsPayloadDocumentationTests
{
    private const string DocumentationPath = "docs/reference/usage-statistics.md";
    private const string FieldTableHeader = "| Field | Type | Example | What it answers |";

    [Fact]
    public void EveryFieldOnTheWire_IsDocumented()
    {
        var documented = ReadDocumentedFieldNames();
        var missing = ReadWireFieldNames().Except(documented, StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            $"{DocumentationPath} does not describe these payload fields: {string.Join(", ", missing)}. "
            + "Every field sent to the vendor needs an entry on that page.");
    }

    [Fact]
    public void EveryDocumentedField_IsActuallyOnTheWire()
    {
        var wire = ReadWireFieldNames();
        var stale = ReadDocumentedFieldNames().Except(wire, StringComparer.Ordinal).ToList();

        Assert.True(
            stale.Count == 0,
            $"{DocumentationPath} describes fields the payload no longer carries: {string.Join(", ", stale)}. "
            + "Remove the entries for fields that are no longer sent.");
    }

    // Counters are reported as range labels rather than numbers, so the page has to list exactly the labels
    // the code can produce.
    [Fact]
    public void EveryBucketLabelTheCodeCanProduce_AppearsInTheDocumentation()
    {
        var contents = ReadDocumentation();

        var labels = UsageStatisticsBuckets.ActiveUserLabels
            .Concat(UsageStatisticsBuckets.PullRequestLabels)
            .Concat(UsageStatisticsBuckets.FindingLabels)
            .Distinct(StringComparer.Ordinal);

        foreach (var label in labels)
        {
            Assert.True(
                contents.Contains($"`{label}`", StringComparison.Ordinal),
                $"{DocumentationPath} does not list the range label '{label}'.");
        }
    }

    // An example no counter can produce documents a value the payload never carries.
    [Fact]
    public void EveryRangeLabelUsedAsAnExample_IsOneThatCounterCanProduce()
    {
        var expected = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["activeUsers"] = UsageStatisticsBuckets.ActiveUserLabels,
            ["pullRequestsPerWeek"] = UsageStatisticsBuckets.PullRequestLabels,
            ["findingsRaisedPerWeek"] = UsageStatisticsBuckets.FindingLabels,
            ["findingsAcceptedPerWeek"] = UsageStatisticsBuckets.FindingLabels,
            ["findingsDismissedPerWeek"] = UsageStatisticsBuckets.FindingLabels,
        };

        foreach (var (field, example) in ReadDocumentedExamples())
        {
            if (!expected.TryGetValue(field, out var labels))
            {
                continue;
            }

            Assert.True(
                labels.Contains(example, StringComparer.Ordinal),
                $"{DocumentationPath} gives '{field}' the example '{example}', which that counter cannot "
                + $"produce. Its labels are {string.Join(", ", labels)}.");
        }
    }

    // The default rather than the resolved value, because a staging build compiles in its own endpoint and
    // the published page describes the release.
    [Fact]
    public void TheDocumentedEndpointAndContact_AreTheOnesAReleaseBuildUses()
    {
        var contents = ReadDocumentation();

        Assert.Contains(UsageStatisticsContract.DefaultPingEndpoint, contents, StringComparison.Ordinal);
        Assert.Contains(UsageStatisticsContract.PrivacyContact, contents, StringComparison.Ordinal);
    }

    // The documentation link the product shows an administrator has to point at the page that exists.
    [Fact]
    public void TheDocumentationLinkInTheProduct_PointsAtThisPage()
    {
        Assert.EndsWith(
            DocumentationPath,
            UsageStatisticsContract.PayloadDocumentationUrl,
            StringComparison.Ordinal);
    }

    private static IReadOnlyCollection<string> ReadWireFieldNames()
    {
        return typeof(UsageStatisticsSnapshot)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property =>
                property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? throw new InvalidOperationException(
                    $"'{property.Name}' has no JsonPropertyName. Every wire field carries an explicit name so "
                    + "a C# rename cannot rename a documented field."))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Reads the field names out of the one table on the page that enumerates them.</summary>
    private static IReadOnlyCollection<string> ReadDocumentedFieldNames()
    {
        var fields = ReadDocumentedExamples().Select(entry => entry.Field).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(fields);
        return fields;
    }

    /// <summary>Reads the field name and the example from each row of the payload field table.</summary>
    private static IReadOnlyList<(string Field, string Example)> ReadDocumentedExamples()
    {
        var lines = ReadDocumentation().Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var headerIndex = Array.FindIndex(lines, line => line.Trim() == FieldTableHeader);

        Assert.True(
            headerIndex >= 0,
            $"{DocumentationPath} no longer contains the payload field table this check reads. Its header row "
            + $"has to stay exactly \"{FieldTableHeader}\", or this check has to be updated for the new format.");

        var rows = new List<(string Field, string Example)>();

        // Skip the header and the separator row beneath it.
        for (var index = headerIndex + 2; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (!line.StartsWith('|'))
            {
                break;
            }

            var cells = line.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            if (cells.Length < 3 || !IsInlineCode(cells[0]))
            {
                continue;
            }

            rows.Add((cells[0].Trim('`'), cells[2].Trim('`')));
        }

        return rows;
    }

    private static bool IsInlineCode(string cell)
    {
        return cell.Length > 2 && cell.StartsWith('`') && cell.EndsWith('`');
    }

    private static string ReadDocumentation()
    {
        return File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), DocumentationPath));
    }

    private static string ResolveRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MeisterDev.ProPR.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
