// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.RegularExpressions;
using MeisterDev.ProPR.Application.Features.CodeInsights.Taxonomy;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Persistence;

/// <summary>
///     Serves the finding-type vocabulary: the fixed core set from code, the custom tags from the database.
///     Validation lives here rather than in the controller so the shadowing rule holds for every caller,
///     including the classifier's own lookups.
/// </summary>
public sealed partial class CodeInsightTaxonomyService(MeisterProPRDbContext dbContext) : ICodeInsightTaxonomyService
{
    private const int MaxSlugLength = 64;
    private const int MaxDisplayNameLength = 128;
    private const int MaxDefinitionLength = 512;

    public Task<CodeInsightTaxonomyDto> GetTaxonomyAsync(Guid clientId, CancellationToken ct = default)
    {
        return this.LoadTaxonomyAsync(clientId, activeOnly: false, ct);
    }

    public Task<CodeInsightTaxonomyDto> GetAssignableTaxonomyAsync(Guid clientId, CancellationToken ct = default)
    {
        return this.LoadTaxonomyAsync(clientId, activeOnly: true, ct);
    }

    public async Task<CodeInsightCustomTagWriteResult> CreateCustomTagAsync(
        Guid clientId,
        CodeInsightCustomTagWriteRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalized = Normalize(request);
        var rejection = Validate(normalized);
        if (rejection is not null)
        {
            return rejection;
        }

        if (await this.SlugIsTakenAsync(clientId, normalized.Slug, excludingTagId: null, ct))
        {
            return SlugAlreadyUsed(normalized.Slug);
        }

        var now = DateTimeOffset.UtcNow;
        var tag = new CodeInsightCustomTag
        {
            Id = Guid.CreateVersion7(),
            ClientId = clientId,
            Slug = normalized.Slug,
            DisplayName = normalized.DisplayName,
            Definition = normalized.Definition,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.CodeInsightCustomTags.Add(tag);
        await dbContext.SaveChangesAsync(ct);

        return CodeInsightCustomTagWriteResult.Success(ToDto(tag));
    }

    public async Task<CodeInsightCustomTagWriteResult> UpdateCustomTagAsync(
        Guid clientId,
        Guid tagId,
        CodeInsightCustomTagWriteRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalized = Normalize(request);
        var rejection = Validate(normalized);
        if (rejection is not null)
        {
            return rejection;
        }

        var tag = await this.FindTagAsync(clientId, tagId, ct);
        if (tag is null)
        {
            return NotFound(tagId);
        }

        if (await this.SlugIsTakenAsync(clientId, normalized.Slug, excludingTagId: tagId, ct))
        {
            return SlugAlreadyUsed(normalized.Slug);
        }

        // Only the tag's own columns change. Assignments reference its identity, so renaming it neither
        // orphans nor relabels any finding that already carries it.
        tag.Slug = normalized.Slug;
        tag.DisplayName = normalized.DisplayName;
        tag.Definition = normalized.Definition;
        tag.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(ct);

        return CodeInsightCustomTagWriteResult.Success(ToDto(tag));
    }

    public async Task<CodeInsightCustomTagWriteResult> RetireCustomTagAsync(
        Guid clientId,
        Guid tagId,
        CancellationToken ct = default)
    {
        var tag = await this.FindTagAsync(clientId, tagId, ct);
        if (tag is null)
        {
            return NotFound(tagId);
        }

        if (tag.RetiredAt is null)
        {
            tag.RetiredAt = DateTimeOffset.UtcNow;
            tag.UpdatedAt = tag.RetiredAt.Value;
            await dbContext.SaveChangesAsync(ct);
        }

        return CodeInsightCustomTagWriteResult.Success(ToDto(tag));
    }

    private async Task<CodeInsightTaxonomyDto> LoadTaxonomyAsync(
        Guid clientId,
        bool activeOnly,
        CancellationToken ct)
    {
        var query = dbContext.CodeInsightCustomTags.Where(tag => tag.ClientId == clientId);
        if (activeOnly)
        {
            query = query.Where(tag => tag.RetiredAt == null);
        }

        var customTags = await query
            .OrderBy(tag => tag.Slug)
            .ToListAsync(ct);

        return new CodeInsightTaxonomyDto(
            CodeInsightCoreTaxonomy.Version,
            CodeInsightCoreTaxonomy.All
                .Select(definition => new CodeInsightCoreTagDto(
                    definition.Slug,
                    definition.DisplayName,
                    definition.Definition,
                    definition.Characteristic,
                    definition.BehaviourChanging))
                .ToList(),
            customTags.Select(ToDto).ToList());
    }

    private Task<CodeInsightCustomTag?> FindTagAsync(Guid clientId, Guid tagId, CancellationToken ct)
    {
        return dbContext.CodeInsightCustomTags
            .FirstOrDefaultAsync(tag => tag.Id == tagId && tag.ClientId == clientId, ct);
    }

    private async Task<bool> SlugIsTakenAsync(
        Guid clientId,
        string slug,
        Guid? excludingTagId,
        CancellationToken ct)
    {
        // Retired tags still hold their slug: reusing one would make a single historical label stand for two
        // different types.
        return await dbContext.CodeInsightCustomTags
            .AnyAsync(
                tag => tag.ClientId == clientId
                       && tag.Slug == slug
                       && (excludingTagId == null || tag.Id != excludingTagId),
                ct);
    }

    private static CodeInsightCustomTagWriteRequest Normalize(CodeInsightCustomTagWriteRequest request)
    {
        return new CodeInsightCustomTagWriteRequest(
            (request.Slug ?? string.Empty).Trim().ToLowerInvariant(),
            (request.DisplayName ?? string.Empty).Trim(),
            (request.Definition ?? string.Empty).Trim());
    }

    private static CodeInsightCustomTagWriteResult? Validate(CodeInsightCustomTagWriteRequest request)
    {
        if (request.Slug.Length == 0 || request.Slug.Length > MaxSlugLength || !SlugPattern().IsMatch(request.Slug))
        {
            return CodeInsightCustomTagWriteResult.Rejected(
                CodeInsightCustomTagWriteError.Invalid,
                $"Slug must be lower-kebab-case (letters, digits and single dashes) and at most {MaxSlugLength} characters.");
        }

        if (request.DisplayName.Length == 0 || request.DisplayName.Length > MaxDisplayNameLength)
        {
            return CodeInsightCustomTagWriteResult.Rejected(
                CodeInsightCustomTagWriteError.Invalid,
                $"Display name is required and must be at most {MaxDisplayNameLength} characters.");
        }

        if (request.Definition.Length == 0 || request.Definition.Length > MaxDefinitionLength)
        {
            return CodeInsightCustomTagWriteResult.Rejected(
                CodeInsightCustomTagWriteError.Invalid,
                $"Definition is required and must be at most {MaxDefinitionLength} characters. The classifier uses it as the label description.");
        }

        if (CodeInsightCoreTaxonomy.IsCoreSlug(request.Slug))
        {
            return CodeInsightCustomTagWriteResult.Rejected(
                CodeInsightCustomTagWriteError.ShadowsCoreTag,
                $"'{request.Slug}' is a core finding type. A custom tag cannot shadow one, because cross-client comparison would become ambiguous.");
        }

        return null;
    }

    private static CodeInsightCustomTagWriteResult SlugAlreadyUsed(string slug)
    {
        return CodeInsightCustomTagWriteResult.Rejected(
            CodeInsightCustomTagWriteError.SlugAlreadyUsed,
            $"This client already has a tag with the slug '{slug}'. Retired tags keep their slug so historical findings stay readable.");
    }

    private static CodeInsightCustomTagWriteResult NotFound(Guid tagId)
    {
        return CodeInsightCustomTagWriteResult.Rejected(
            CodeInsightCustomTagWriteError.NotFound,
            $"Custom tag '{tagId}' does not exist for this client.");
    }

    private static CodeInsightCustomTagDto ToDto(CodeInsightCustomTag tag)
    {
        return new CodeInsightCustomTagDto(
            tag.Id,
            tag.Slug,
            tag.DisplayName,
            tag.Definition,
            tag.RetiredAt,
            tag.CreatedAt,
            tag.UpdatedAt);
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
