// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.ProCursor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AI.ProCursor;

/// <summary>
///     Extracts fixed-size searchable text chunks from a materialized ProCursor source state.
/// </summary>
public sealed class ProCursorChunkExtractor(
    IOptions<ProCursorOptions> options,
    ILogger<ProCursorChunkExtractor> logger) : IProCursorChunkExtractor
{
    private readonly ProCursorOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProCursorExtractedChunk>> ExtractAsync(
        ProCursorKnowledgeSource source,
        ProCursorMaterializedSource materializedSource,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(materializedSource);

        var targetLines = Math.Max(10, this._options.ChunkTargetLines);
        var extractedChunks = new List<ProCursorExtractedChunk>();

        foreach (var sourcePath in materializedSource.MaterializedPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            if (BinaryFileDetector.IsBinary(sourcePath))
            {
                continue;
            }

            var absolutePath = Path.Combine(
                materializedSource.RootDirectory,
                sourcePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(absolutePath))
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(absolutePath, ct);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var normalizedContent = NormalizeLineEndings(content);
            var lines = normalizedContent.Split('\n');
            var chunkKind = DetermineChunkKind(source, sourcePath);
            var title = DetermineTitle(sourcePath, normalizedContent, chunkKind);
            var chunkOrdinal = 0;

            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex += targetLines)
            {
                var sliceLength = Math.Min(targetLines, lines.Length - lineIndex);
                var slice = string.Join('\n', lines.Skip(lineIndex).Take(sliceLength)).Trim();
                if (string.IsNullOrWhiteSpace(slice))
                {
                    continue;
                }

                extractedChunks.Add(new ProCursorExtractedChunk(
                    sourcePath,
                    chunkKind,
                    title,
                    chunkOrdinal++,
                    lineIndex + 1,
                    lineIndex + sliceLength,
                    ComputeContentHash(slice),
                    slice));
            }
        }

        logger.LogInformation(
            "Extracted {ChunkCount} ProCursor chunks from {FileCount} materialized files for source {SourceId}.",
            extractedChunks.Count,
            materializedSource.MaterializedPaths.Count,
            source.Id);

        return extractedChunks.AsReadOnly();
    }

    private static string DetermineChunkKind(ProCursorKnowledgeSource source, string sourcePath)
    {
        if (source.SourceKind == Domain.Enums.ProCursorSourceKind.AdoWiki)
        {
            return "wiki_page";
        }

        var fileName = Path.GetFileName(sourcePath);
        var extension = Path.GetExtension(sourcePath);

        if (fileName.StartsWith("readme", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".mdx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
        {
            return "readme";
        }

        return "code_file";
    }

    private static string? DetermineTitle(string sourcePath, string content, string chunkKind)
    {
        if (string.Equals(chunkKind, "wiki_page", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(chunkKind, "readme", StringComparison.OrdinalIgnoreCase))
        {
            var heading = content
                .Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith('#'));

            if (!string.IsNullOrWhiteSpace(heading))
            {
                return heading.TrimStart('#').Trim();
            }
        }

        return Path.GetFileName(sourcePath);
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static string ComputeContentHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
