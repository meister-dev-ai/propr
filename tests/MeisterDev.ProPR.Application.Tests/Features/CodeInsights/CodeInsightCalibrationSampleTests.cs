// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.RegularExpressions;
using MeisterDev.ProPR.Application.Features.CodeInsights.Taxonomy;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Tests.Features.CodeInsights;

/// <summary>
///     The calibration sample is drawn by a SQL script, which has to know the functional-versus-evolvability
///     split the application derives in code. That is a second copy of one rule, and a second copy nobody
///     checks is a rule that drifts: a sample stratified by the wrong classes measures agreement on a
///     population that does not match what the product reports. This test is the check.
/// </summary>
public sealed class CodeInsightCalibrationSampleTests
{
    private const string ScriptPath = "scripts/code-insight-calibration-sample.sql";

    [Fact]
    public void TheSampleScriptSplitsConcernClassesExactlyAsTheApplicationDoes()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), ScriptPath));

        var mapped = Regex
            .Matches(script, @"\('(?<slug>[a-z-]+)',\s*'(?<class>functional|evolvability)'\)")
            .Select(match => (Slug: match.Groups["slug"].Value, Class: match.Groups["class"].Value))
            .ToDictionary(entry => entry.Slug, entry => entry.Class, StringComparer.Ordinal);

        Assert.NotEmpty(mapped);

        // Every core type appears, so a type added to the taxonomy cannot be silently left out of the sample.
        Assert.Equal(
            CodeInsightCoreTaxonomy.AllSlugs.OrderBy(slug => slug, StringComparer.Ordinal),
            mapped.Keys.OrderBy(slug => slug, StringComparer.Ordinal));

        foreach (var definition in CodeInsightCoreTaxonomy.All)
        {
            var expected = CodeInsightCoreTaxonomy.ConcernClassOf(definition.Characteristic) switch
            {
                CodeInsightConcernClass.Functional => "functional",
                _ => "evolvability",
            };

            Assert.Equal(expected, mapped[definition.Slug]);
        }
    }

    [Fact]
    public void TheSampleScriptNeverShowsALabellerTheAnswerItIsCheckedAgainst()
    {
        // A labeller who can see the recorded outcome or reason is no longer independent, and agreement
        // measured against a primed labeller says nothing. The stratum key names the outcome, which is
        // unavoidable, so the projection must not also carry the reason or the model's confidence.
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), ScriptPath));

        Assert.DoesNotContain("rejection_reason", script, StringComparison.Ordinal);
        Assert.DoesNotContain("classifier_confidence", script, StringComparison.Ordinal);
        Assert.DoesNotContain("encrypted_message", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSampleScriptDrawsDeterministicallyAndWritesNothing()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), ScriptPath));

        // Seeded hash ordering is what makes two people drawing the same sample possible at all.
        Assert.Contains("md5(:'seed' || finding_id::text)", script, StringComparison.Ordinal);

        // Safe to run against a replica, and incapable of changing the data it is measuring.
        foreach (var forbidden in new[] { "INSERT", "UPDATE ", "DELETE", "CREATE TABLE", "DROP" })
        {
            Assert.DoesNotContain(forbidden, script, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    ///     Walks up from the test assembly until the solution file appears. The tests run from an output
    ///     directory whose depth depends on the configuration and the runner, so the path cannot be a constant.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MeisterDev.ProPR.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
