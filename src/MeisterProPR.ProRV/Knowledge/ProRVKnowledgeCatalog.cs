// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeisterProPR.ProRV.Knowledge;

internal interface IProRVKnowledgeCatalog
{
    IReadOnlyList<ProRVCheckDefinition> GetCheckIndex(string language);

    string GetInstruction(string language, string checkId);
}

internal sealed class EmbeddedProRVKnowledgeCatalog : IProRVKnowledgeCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string ResourceRoot = "MeisterProPR.ProRV.Assets.";

    private readonly Assembly assembly = typeof(EmbeddedProRVKnowledgeCatalog).Assembly;
    private readonly ConcurrentDictionary<string, ProRVIndexDocument> indexCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> instructionCache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ProRVCheckDefinition> GetCheckIndex(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        return this.indexCache.GetOrAdd(language, this.LoadIndex).Checks;
    }

    public string GetInstruction(string language, string checkId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkId);

        var check = this.GetCheckIndex(language)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, checkId, StringComparison.Ordinal));
        if (check is null)
        {
            throw new InvalidOperationException($"No ProRV check '{checkId}' was found for language '{language}'.");
        }

        var cacheKey = $"{language}:{check.InstructionPath}";
        return this.instructionCache.GetOrAdd(cacheKey, _ => this.LoadTextResource(language, check.InstructionPath));
    }

    private ProRVIndexDocument LoadIndex(string language)
    {
        var resourceName = BuildResourceName(language, "index.json");
        using var stream = this.assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded ProRV resource '{resourceName}' was not found.");

        var document = JsonSerializer.Deserialize<ProRVIndexDocument>(stream, SerializerOptions)
            ?? throw new InvalidOperationException($"Embedded ProRV index '{resourceName}' could not be deserialized.");

        if (!string.Equals(document.Language, language, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Embedded ProRV index '{resourceName}' declared language '{document.Language}', expected '{language}'.");
        }

        return document;
    }

    private string LoadTextResource(string language, string relativePath)
    {
        var stream = OpenResourceStream(language, relativePath, out var resourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private Stream OpenResourceStream(string language, string relativePath, out string resourceName)
    {
        resourceName = BuildResourceName(language, relativePath);
        var stream = this.assembly.GetManifestResourceStream(resourceName);
        if (stream is not null)
        {
            return stream;
        }

        var normalizedResourceName = BuildNormalizedResourceName(language, relativePath);
        if (!string.Equals(normalizedResourceName, resourceName, StringComparison.Ordinal))
        {
            stream = this.assembly.GetManifestResourceStream(normalizedResourceName);
            if (stream is not null)
            {
                resourceName = normalizedResourceName;
                return stream;
            }
        }

        throw new InvalidOperationException($"Embedded ProRV resource '{resourceName}' was not found.");
    }

    private static string BuildResourceName(string language, string relativePath)
    {
        var normalizedPath = relativePath.Replace('/', '.').Replace('\\', '.');
        return string.Concat(ResourceRoot, language, ".", normalizedPath);
    }

    private static string BuildNormalizedResourceName(string language, string relativePath)
    {
        var parts = relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length <= 1)
        {
            return BuildResourceName(language, relativePath);
        }

        for (var index = 0; index < parts.Length - 1; index++)
        {
            parts[index] = parts[index].Replace('-', '_');
        }

        return string.Concat(ResourceRoot, language, ".", string.Join('.', parts));
    }
}

internal sealed class ProRVIndexDocument
{
    [JsonPropertyName("language")]
    public string Language { get; init; } = string.Empty;

    [JsonPropertyName("checks")]
    public IReadOnlyList<ProRVCheckDefinition> Checks { get; init; } = [];
}

internal sealed class ProRVCheckDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("shortDescription")]
    public string ShortDescription { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = string.Empty;

    [JsonPropertyName("precision")]
    public string Precision { get; init; } = string.Empty;

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    [JsonPropertyName("instructionPath")]
    public string InstructionPath { get; init; } = string.Empty;

    [JsonPropertyName("sourceRelativePath")]
    public string SourceRelativePath { get; init; } = string.Empty;

    [JsonPropertyName("referenceExampleFileNames")]
    public IReadOnlyList<string> ReferenceExampleFileNames { get; init; } = [];
}
