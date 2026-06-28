// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Guards the assumption the <c>result_summary</c> backfill relies on: the <see cref="ReviewResult" />
///     stored in <c>result_json</c> serializes its summary under the PascalCase key <c>"Summary"</c>.
///     If the converter ever switched to web (camelCase) options, the migration's
///     <c>result_json -&gt;&gt; 'Summary'</c> backfill would silently produce nulls.
/// </summary>
public sealed class ReviewResultJsonConversionTests
{
    [Fact]
    public void EfConverter_SerializesReviewResult_WithPascalCaseSummaryKey()
    {
        var converter = GetResultConverter();
        var result = new ReviewResult(
            "the summary text",
            [new ReviewComment(null, null, CommentSeverity.Info, "a comment")]);

        var stored = (string?)converter.ConvertToProvider(result);

        Assert.NotNull(stored);
        Assert.Contains("\"Summary\"", stored);
        Assert.DoesNotContain("\"summary\"", stored);

        // The PascalCase key resolves the value, mirroring the backfill SQL operator.
        using var document = JsonDocument.Parse(stored!);
        Assert.Equal("the summary text", document.RootElement.GetProperty("Summary").GetString());

        // And round-trips back through the converter unchanged.
        var roundTripped = (ReviewResult?)converter.ConvertFromProvider(stored);
        Assert.Equal("the summary text", roundTripped!.Summary);
    }

    private static ValueConverter GetResultConverter()
    {
        // Building the model is offline metadata work — no database connection is opened.
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused", o => o.UseVector())
            .Options;
        using var context = new MeisterProPRDbContext(options);
        var property = context.Model
            .FindEntityType(typeof(ReviewJob))!
            .FindProperty(nameof(ReviewJob.Result))!;
        return property.GetValueConverter()!;
    }
}
