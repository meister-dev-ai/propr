// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.AI;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class PreparedPromptTemplateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"propr-prepared-prompts-{Guid.NewGuid():N}");

    [Fact]
    public async Task PreparedTemplate_ReusesItsDelegateWithoutReadingFilesAndIsolatesConcurrentModels()
    {
        var templateRoot = Path.Combine(this._root, "AI", "Prompts");
        Directory.CreateDirectory(Path.Combine(templateRoot, "shared", "partials"));
        File.WriteAllText(Path.Combine(templateRoot, "probe.hbs"), "{{> prefix}} {{{value}}}");
        File.WriteAllText(Path.Combine(templateRoot, "shared", "partials", "prefix.hbs"), "Source:");
        var prepared = PromptTemplateRuntime.PrepareTemplateFile("probe.hbs", new PromptTemplateFileProvider(this._root));

        var render = prepared.Value;
        Directory.Delete(this._root, true);
        var values = Enumerable.Range(0, 32).Select(index => $"{{{{token{index}}}}} <tag> `code`\r\nnext").ToArray();

        var outputs = await Task.WhenAll(values.Select(value => Task.Run(() => prepared.Value(new { value }))));

        Assert.Same(render, prepared.Value);
        Assert.Equal(values.Select(value => "Source: " + value), outputs);
    }

    [Fact]
    public void PrepareTemplateFile_LoadsOnFirstUseAndReportsTheTemplateOnFailure()
    {
        var prepared = PromptTemplateRuntime.PrepareTemplateFile("missing.hbs", new PromptTemplateFileProvider(this._root));

        Assert.False(prepared.IsValueCreated);
        var error = Assert.Throws<InvalidOperationException>(() => { _ = prepared.Value; });

        Assert.Contains("missing.hbs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreparedRenderer_RetainsItsOwnPartialsAfterOtherRenders()
    {
        var renderer = new HandlebarsPromptRenderer();
        var first = renderer.Prepare("{{> probe}} {{{value}}}", new Dictionary<string, string> { ["probe"] = "first" });

        Assert.Equal("second x", renderer.Render("{{> probe}} {{value}}", new { value = "x" }, new Dictionary<string, string> { ["probe"] = "second" }));
        Assert.Equal("first <x>", first(new { value = "<x>" }));
        Assert.Equal("first {{x}}", first(new { value = "{{x}}" }));
    }

    [Fact]
    public async Task ConcurrentFirstUse_PreparesDistinctTemplatesAndRetainsEachCompiledDelegate()
    {
        var templateRoot = Path.Combine(this._root, "AI", "Prompts");
        Directory.CreateDirectory(Path.Combine(templateRoot, "shared", "partials"));
        File.WriteAllText(Path.Combine(templateRoot, "shared", "partials", "prefix.hbs"), "Source:");
        var files = new PromptTemplateFileProvider(this._root);
        var templates = Enumerable.Range(0, 3).Select(index =>
        {
            var path = $"probe-{index}.hbs";
            File.WriteAllText(Path.Combine(templateRoot, path), "{{> prefix}} " + index + " {{{value}}}");
            return PromptTemplateRuntime.PrepareTemplateFile(path, files);
        }).ToArray();
        Assert.All(templates, template => Assert.False(template.IsValueCreated));
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 24).Select(index => Task.Run(() =>
        {
            start.Wait();
            var templateIndex = index % templates.Length;
            var render = templates[templateIndex].Value;
            Assert.Equal($"Source: {templateIndex} value-{index}", render(new { value = $"value-{index}" }));
            return (templateIndex, render);
        })).ToArray();

        start.Set();
        var results = await Task.WhenAll(tasks);
        Directory.Delete(this._root, true);

        foreach (var (templateIndex, render) in results)
        {
            Assert.Same(templates[templateIndex].Value, render);
            Assert.Equal($"Source: {templateIndex} <next>", render(new { value = "<next>" }));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(this._root))
        {
            Directory.Delete(this._root, true);
        }
    }
}
