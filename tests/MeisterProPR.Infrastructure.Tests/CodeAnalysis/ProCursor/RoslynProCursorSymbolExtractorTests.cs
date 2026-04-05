// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Infrastructure.CodeAnalysis.ProCursor;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterProPR.Infrastructure.Tests.CodeAnalysis.ProCursor;

/// <summary>
///     Tests the Roslyn-backed ProCursor extractor against controlled C# source material.
/// </summary>
public sealed class RoslynProCursorSymbolExtractorTests
{
    [Fact]
    public async Task ExtractAsync_WithCSharpRepositoryState_ReturnsSymbolsAndRelations()
    {
        var rootDirectory = CreateRootDirectory();
        var snapshotId = Guid.NewGuid();

        try
        {
            await WriteFileAsync(rootDirectory, "/src/Greeter.cs", """
                namespace Demo;

                public interface IMessageSink
                {
                    void Write(string value);
                }

                public sealed class Greeter : IMessageSink
                {
                    public void Write(string value)
                    {
                    }

                    public void Run()
                    {
                        this.Write("hi");
                    }
                }
                """);

            var extractor = new RoslynProCursorSymbolExtractor(NullLogger<RoslynProCursorSymbolExtractor>.Instance);
            var result = await extractor.ExtractAsync(
                new ProCursorMaterializedSource(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "main",
                    "commit-1",
                    rootDirectory,
                    ["/src/Greeter.cs"]),
                snapshotId,
                CancellationToken.None);

            Assert.True(result.SupportsSymbolQueries);
            Assert.Null(result.UnsupportedReason);
            Assert.Contains(result.Symbols, symbol => symbol.SnapshotId == snapshotId && symbol.DisplayName == "IMessageSink");
            Assert.Contains(result.Symbols, symbol => symbol.SnapshotId == snapshotId && symbol.DisplayName == "Greeter");
            Assert.Contains(result.Symbols, symbol => symbol.SnapshotId == snapshotId && symbol.DisplayName == "Write");
            Assert.Contains(result.Symbols, symbol => symbol.SnapshotId == snapshotId && symbol.DisplayName == "Run");

            var greeterType = result.Symbols.Single(symbol => symbol.DisplayName == "Greeter" && symbol.SymbolKind == "type");
            var sinkType = result.Symbols.Single(symbol => symbol.DisplayName == "IMessageSink" && symbol.SymbolKind == "type");
            var runMethod = result.Symbols.Single(symbol => symbol.DisplayName == "Run" && symbol.SymbolKind == "method");
            var writeMethod = result.Symbols.Single(symbol => symbol.DisplayName == "Write" && symbol.SymbolKind == "method" && symbol.ContainingSymbolKey == greeterType.SymbolKey);

            Assert.Contains(result.Edges, edge =>
                edge.SnapshotId == snapshotId &&
                edge.FromSymbolKey == greeterType.SymbolKey &&
                edge.ToSymbolKey == sinkType.SymbolKey &&
                edge.EdgeKind == "implementation");
            Assert.Contains(result.Edges, edge =>
                edge.SnapshotId == snapshotId &&
                edge.FromSymbolKey == greeterType.SymbolKey &&
                edge.ToSymbolKey == runMethod.SymbolKey &&
                edge.EdgeKind == "containment");
            Assert.Contains(result.Edges, edge =>
                edge.SnapshotId == snapshotId &&
                edge.FromSymbolKey == runMethod.SymbolKey &&
                edge.ToSymbolKey == writeMethod.SymbolKey &&
                edge.EdgeKind == "call");
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    [Fact]
    public async Task ExtractAsync_WithoutSupportedSourceFiles_ReturnsUnsupportedLanguage()
    {
        var rootDirectory = CreateRootDirectory();

        try
        {
            await WriteFileAsync(rootDirectory, "/docs/readme.md", "# Hello\n");

            var extractor = new RoslynProCursorSymbolExtractor(NullLogger<RoslynProCursorSymbolExtractor>.Instance);
            var result = await extractor.ExtractAsync(
                new ProCursorMaterializedSource(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "main",
                    "commit-1",
                    rootDirectory,
                    ["/docs/readme.md"]),
                Guid.NewGuid(),
                CancellationToken.None);

            Assert.False(result.SupportsSymbolQueries);
            Assert.Equal("unsupported_language", result.UnsupportedReason);
            Assert.Empty(result.Symbols);
            Assert.Empty(result.Edges);
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    private static string CreateRootDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "meisterpropr-procursor-symbol-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WriteFileAsync(string rootDirectory, string relativePath, string content)
    {
        var filePath = Path.Combine(rootDirectory, relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(filePath, content);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}
