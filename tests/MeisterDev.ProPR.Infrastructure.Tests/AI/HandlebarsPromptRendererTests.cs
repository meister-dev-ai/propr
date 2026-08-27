// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.AI;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

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

    [Fact]
    public void Render_WhenAnotherPartialSetRedefinesTheSameName_KeepsEachRenderIndependent()
    {
        // The renderer reuses one Handlebars environment per partial set instead of building one per render.
        // Reuse is only safe while a render cannot observe a partial another caller registered, so pin that: the
        // same template and partial set must produce the same text after an intervening render that binds the same
        // partial name to different content.
        var renderer = new HandlebarsPromptRenderer();
        const string Template = "value: {{> independence-probe}}";

        var first = renderer.Render(
            Template,
            new { },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["independence-probe"] = "alpha" });

        var intervening = renderer.Render(
            Template,
            new { },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["independence-probe"] = "beta" });

        var repeat = renderer.Render(
            Template,
            new { },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["independence-probe"] = "alpha" });

        Assert.Equal("value: alpha", first);
        Assert.Equal("value: beta", intervening);
        Assert.Equal("value: alpha", repeat);
    }

    [Fact]
    public void Render_WhenTwoPartialSetsShareAJoinedForm_DoesNotReuseTheOthersEnvironment()
    {
        // Two partial sets must never be treated as one. Identifying a set by a digest over its names and values
        // joined together does exactly that whenever a value contains the join character: the join of
        // {"collide-a": "x", "collide-b": "y"} equals the join of {"collide-a": "x<RS>collide-b<US>y"}, and the
        // second set then renders with the first set's partials. Partial names are restricted to word characters,
        // but values are raw template text, so this is reachable.
        var renderer = new HandlebarsPromptRenderer();

        var twoPartials = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["collide-a"] = "x",
            ["collide-b"] = "y",
        };

        var onePartialCarryingSeparators = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["collide-a"] = "x\u001ecollide-b\u001fy",
        };

        var two = renderer.Render("{{> collide-a}}|{{> collide-b}}", new { }, twoPartials);
        var one = renderer.Render("{{> collide-a}}", new { }, onePartialCarryingSeparators);

        Assert.Equal("x|y", two);
        Assert.Equal("x\u001ecollide-b\u001fy", one);
    }

    [Fact]
    public void Render_WhenRepeatedForOnePartialSet_ReusesOneEnvironment()
    {
        // Building an environment per render is what made the cost of a render grow with the number of renders the
        // process had already performed, without bound, until it was restarted. A render must therefore resolve
        // through the cache, and repeated renders must keep the entry the first one populated. The assertions are
        // keyed to this partial set, so what other tests render in the same process cannot affect them.
        var renderer = new HandlebarsPromptRenderer();
        var partials = new Dictionary<string, string>(StringComparer.Ordinal) { ["reuse-probe"] = "text" };

        Assert.Null(renderer.GetCachedEnvironment(partials));

        renderer.Render("value: {{> reuse-probe}}", new { }, partials);
        var afterFirstRender = renderer.GetCachedEnvironment(partials);
        Assert.NotNull(afterFirstRender);

        for (var i = 0; i < 200; i++)
        {
            renderer.Render("value: {{> reuse-probe}}", new { }, partials);
        }

        Assert.Same(afterFirstRender, renderer.GetCachedEnvironment(partials));
    }

    [Fact]
    public void GetCachedEnvironment_WhenThePartialSetDiffers_DoesNotOfferTheHeldEnvironment()
    {
        // The held environment is reused only for a partial set matching the one it was built from, and that
        // comparison is what keeps a render from compiling against another render's partials. Pinning it directly
        // settles the property without depending on threads overlapping, which no barrier can guarantee: the
        // concurrent test exercises the same mechanism under load but cannot establish it.
        var renderer = new HandlebarsPromptRenderer();
        var held = new Dictionary<string, string>(StringComparer.Ordinal) { ["compare-probe"] = "alpha" };

        renderer.Render("value: {{> compare-probe}}", new { }, held);
        Assert.NotNull(renderer.GetCachedEnvironment(held));

        // Same name, different text.
        Assert.Null(renderer.GetCachedEnvironment(new Dictionary<string, string>(StringComparer.Ordinal) { ["compare-probe"] = "beta" }));

        // A superset, and a subset.
        Assert.Null(
            renderer.GetCachedEnvironment(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["compare-probe"] = "alpha",
                    ["compare-probe-extra"] = "alpha",
                }));
        Assert.Null(renderer.GetCachedEnvironment(new Dictionary<string, string>(StringComparer.Ordinal)));
        Assert.Null(renderer.GetCachedEnvironment(null));

        // An equal set built independently still matches, so reuse does not depend on the caller's instance.
        Assert.NotNull(renderer.GetCachedEnvironment(new Dictionary<string, string>(StringComparer.Ordinal) { ["compare-probe"] = "alpha" }));
    }

    [Fact]
    public async Task Render_WhenCalledConcurrentlyWithDifferentPartialSets_ProducesEachCallersOwnText()
    {
        // Several file passes render prompts at the same time, so the environments are reached from many threads at
        // once. Each caller must get the text its own partial set produces.
        //
        // The workers meet at a barrier on dedicated threads and are released together, and each then renders
        // repeatedly rather than once. Overlap is what gives this test its power and no barrier can guarantee it,
        // since the operating system decides what runs when; many renders per worker is what makes a schedule
        // without overlap vanishingly unlikely. Forcing overlap outright would mean a synchronisation seam inside
        // Render itself, which is not worth carrying in the rendering path for a test. Against the implementation
        // this replaced, which registered partials into one shared registry, this shape failed every run.
        const int Workers = 32;
        const int RendersPerWorker = 25;
        var renderer = new HandlebarsPromptRenderer();
        const string Template = "value: {{> concurrent-probe}}";

        using var start = new Barrier(Workers);

        var tasks = Enumerable.Range(0, Workers).Select(index => Task.Factory.StartNew(
            () =>
            {
                var expected = $"content-{index % 4}";
                var partials = new Dictionary<string, string>(StringComparer.Ordinal) { ["concurrent-probe"] = expected };
                start.SignalAndWait();

                for (var i = 0; i < RendersPerWorker; i++)
                {
                    var actual = renderer.Render(Template, new { }, partials);
                    if (!string.Equals(actual, $"value: {expected}", StringComparison.Ordinal))
                    {
                        return (Expected: $"value: {expected}", Actual: actual);
                    }
                }

                return (Expected: $"value: {expected}", Actual: $"value: {expected}");
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default));

        foreach (var result in await Task.WhenAll(tasks))
        {
            Assert.Equal(result.Expected, result.Actual);
        }
    }
}
