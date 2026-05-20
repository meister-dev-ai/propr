// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class HandlebarsPromptRendererTests
{
    [Fact]
    public void Render_WhenTemplateUsesScalarProperties_RendersExpectedText()
    {
        var renderer = new HandlebarsPromptRenderer();

        var result = renderer.Render(
            "Review {{filePath}} ({{fileIndex}} of {{totalFiles}})", new
            {
                filePath = "src/Foo.cs",
                fileIndex = 2,
                totalFiles = 5,
            });

        Assert.Equal("Review src/Foo.cs (2 of 5)", result);
    }

    [Fact]
    public void Render_WhenTemplateUsesSharedPartial_RendersPartialContent()
    {
        var renderer = new HandlebarsPromptRenderer();

        var result = renderer.Render(
            "Before {{> renderer-test-reminder}} After",
            new { key = "comments" },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["renderer-test-reminder"] = "{{key}} only",
            });

        Assert.Equal("Before comments only After", result);
    }

    [Fact]
    public void Render_WhenTemplateUsesCollectionBlock_RendersAllItems()
    {
        var renderer = new HandlebarsPromptRenderer();

        var result = renderer.Render(
            "{{#each files}}- {{this}}\n{{/each}}",
            new
            {
                files = new[] { "src/Foo.cs", "src/Bar.cs" },
            });

        Assert.Equal("- src/Foo.cs\n- src/Bar.cs\n", result);
    }

    [Fact]
    public void Render_WhenPartialReferenceIsMissing_ThrowsInvalidOperationException()
    {
        var renderer = new HandlebarsPromptRenderer();

        var ex = Assert.Throws<InvalidOperationException>(() => renderer.Render("{{> missing-partial}}", new { }));

        Assert.Contains("missing-partial", ex.Message, StringComparison.Ordinal);
    }
}
