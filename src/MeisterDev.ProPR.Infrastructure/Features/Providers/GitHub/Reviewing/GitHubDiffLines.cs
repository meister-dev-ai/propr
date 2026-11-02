// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text.RegularExpressions;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

/// <summary>
///     The lines of a pull request diff that GitHub accepts for a review comment on the right side. GitHub
///     resolves a review comment against the diff of the reviewed commit and refuses the whole review with
///     422 "Line could not be resolved" when one comment names a line outside that diff. Added lines and the
///     context lines inside a hunk are accepted; removed lines exist on the left side only.
/// </summary>
internal sealed partial class GitHubDiffLines
{
    private readonly IReadOnlyDictionary<string, IReadOnlySet<int>> _linesByPath;
    private readonly bool _complete;

    private GitHubDiffLines(IReadOnlyDictionary<string, IReadOnlySet<int>> linesByPath, bool complete)
    {
        this._linesByPath = linesByPath;
        this._complete = complete;
    }

    /// <summary>A diff without any line that accepts a comment, which places every finding in the summary.</summary>
    public static GitHubDiffLines None { get; } = new(new Dictionary<string, IReadOnlySet<int>>(StringComparer.Ordinal), complete: true);

    /// <summary>
    ///     Builds the accepted lines from the changed files of a comparison.
    /// </summary>
    /// <param name="files">Each changed file's path and patch. GitHub omits the patch of a binary file and of a very large diff.</param>
    /// <param name="complete">
    ///     Whether the listing names every changed file. When it does not, a file missing from it is unknown,
    ///     and a comment on that file is sent as before so that GitHub decides.
    /// </param>
    public static GitHubDiffLines FromFiles(IEnumerable<(string Path, string? Patch)> files, bool complete)
    {
        ArgumentNullException.ThrowIfNull(files);

        var linesByPath = new Dictionary<string, IReadOnlySet<int>>(StringComparer.Ordinal);
        foreach (var (path, patch) in files)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                linesByPath[NormalizePath(path)] = ParseRightLines(patch);
            }
        }

        return new GitHubDiffLines(linesByPath, complete);
    }

    /// <summary>Whether GitHub accepts a review comment on <paramref name="line" /> of the new version of <paramref name="path" />.</summary>
    public bool Accepts(string path, int line)
    {
        if (line <= 0 || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return this._linesByPath.TryGetValue(NormalizePath(path), out var lines)
            ? lines.Contains(line)
            : !this._complete;
    }

    /// <summary>Returns the new-version line numbers of the added and context lines of a unified diff patch.</summary>
    internal static IReadOnlySet<int> ParseRightLines(string? patch)
    {
        var lines = new HashSet<int>();
        if (string.IsNullOrEmpty(patch))
        {
            return lines;
        }

        // The counts of the hunk header bound the hunk. Following them keeps the numbering right when a
        // context line that is empty arrives without its leading space.
        var next = 0;
        var leftRemaining = 0;
        var rightRemaining = 0;
        foreach (var raw in patch.Split('\n'))
        {
            var text = raw.TrimEnd('\r');
            var header = HunkHeader().Match(text);
            if (header.Success)
            {
                next = ReadNumber(header.Groups["rightStart"], 0);
                leftRemaining = ReadNumber(header.Groups["leftCount"], 1);
                rightRemaining = ReadNumber(header.Groups["rightCount"], 1);
                continue;
            }

            if (leftRemaining <= 0 && rightRemaining <= 0)
            {
                continue;
            }

            switch (text.Length == 0 ? ' ' : text[0])
            {
                case '+':
                    lines.Add(next++);
                    rightRemaining--;
                    break;
                case '-':
                    leftRemaining--;
                    break;
                case ' ':
                    lines.Add(next++);
                    leftRemaining--;
                    rightRemaining--;
                    break;
            }
        }

        return lines;
    }

    private static int ReadNumber(Group group, int absent)
    {
        return group.Success ? int.Parse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture) : absent;
    }

    private static string NormalizePath(string path)
    {
        return path.Trim().TrimStart('/');
    }

    [GeneratedRegex(@"^@@ -\d+(?:,(?<leftCount>\d+))? \+(?<rightStart>\d+)(?:,(?<rightCount>\d+))? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();
}
