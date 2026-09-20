// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;

namespace MeisterDev.Ai.Providers.Conformance;

/// <summary>Every check's outcome for one family.</summary>
/// <remarks>
///     A report is produced whatever the family did, including throwing: a check that threw is recorded as a
///     failure naming the exception. The host runs these while it starts, where a throw would be
///     indistinguishable from a load error and would take a family out without saying which check it was.
/// </remarks>
public sealed record ConformanceReport
{
    /// <summary>Builds a report over the results of one run.</summary>
    /// <param name="family">How the family is named in the report, usually its identity key.</param>
    /// <param name="results">One result per check, in the order the checks ran.</param>
    public ConformanceReport(string family, IEnumerable<ConformanceResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        this.Family = family;
        this.Results = results.ToImmutableArray();
    }

    /// <summary>How the family is named in the report.</summary>
    public string Family { get; }

    /// <summary>One result per check, in the order the checks ran.</summary>
    public IReadOnlyList<ConformanceResult> Results { get; }

    /// <summary>The checks the family did not satisfy.</summary>
    public IReadOnlyList<ConformanceResult> Failures =>
        [.. this.Results.Where(result => result.IsFailure)];

    /// <summary>Whether the family satisfied every check that ran.</summary>
    public bool Passed => !this.Results.Any(result => result.IsFailure);

    /// <summary>
    ///     The failures as one line, for a log entry or the reason recorded beside a skipped family. Empty for a
    ///     family that passed.
    /// </summary>
    public string Summary =>
        string.Join(" ", this.Failures.Select(failure => $"'{failure.Check}': {failure.Detail}"));

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Passed
            ? $"{this.Family}: passed {this.Results.Count} checks"
            : $"{this.Family}: {this.Summary}";
    }
}
