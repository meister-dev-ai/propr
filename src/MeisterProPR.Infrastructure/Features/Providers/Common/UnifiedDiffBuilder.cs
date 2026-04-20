// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

internal static class UnifiedDiffBuilder
{
    public static string Build(string oldContent, string newContent)
    {
        var diff = InlineDiffBuilder.Diff(oldContent ?? string.Empty, newContent ?? string.Empty);
        var builder = new StringBuilder();

        foreach (var line in diff.Lines)
        {
            var prefix = line.Type switch
            {
                ChangeType.Inserted => "+ ",
                ChangeType.Deleted => "- ",
                _ => "  ",
            };

            builder.AppendLine($"{prefix}{line.Text}");
        }

        return builder.ToString();
    }
}
