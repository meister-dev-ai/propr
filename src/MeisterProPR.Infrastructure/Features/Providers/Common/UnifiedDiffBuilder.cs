// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using DiffPlex.Renderer;

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

internal static class UnifiedDiffBuilder
{
    public static string Build(string oldContent, string newContent, string filePath)
    {
        var name = string.IsNullOrEmpty(filePath) ? "file" : filePath;
        var renderer = new UnidiffRenderer();

        return renderer.Generate(
            TrimSingleTrailingNewline(oldContent ?? string.Empty),
            TrimSingleTrailingNewline(newContent ?? string.Empty),
            $"a/{name}",
            $"b/{name}",
            false);
    }

    // Real file content normally ends in a line terminator. DiffPlex's line splitter treats that
    // trailing terminator as an extra empty final line, which would surface as a phantom blank
    // +/- line and inflate the hunk line count by one. Dropping one trailing CRLF or LF makes the
    // output match what git emits for the same content.
    private static string TrimSingleTrailingNewline(string content)
    {
        if (content.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return content[..^2];
        }

        if (content.EndsWith("\n", StringComparison.Ordinal))
        {
            return content[..^1];
        }

        return content;
    }
}
