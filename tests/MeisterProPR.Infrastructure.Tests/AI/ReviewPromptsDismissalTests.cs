// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Tests asserting that <see cref="ReviewPrompts.BuildSystemPrompt" /> injects dismissed patterns
///     into the AI system prompt (US3, T021).
/// </summary>
public class ReviewPromptsDismissalTests
{
    // T021 — Non-empty DismissedPatterns → output contains the exclusion section heading
    [Fact]
    public void BuildSystemPrompt_WithDismissedPatterns_ContainsDismissedPatternsSectionHeading()
    {
        var context = new ReviewSystemContext(null, Array.Empty<RepositoryInstruction>(), null)
        {
            DismissedPatterns = new List<string> { "use idisposable pattern" }.AsReadOnly(),
        };

        var result = ReviewPrompts.BuildSystemPrompt(context);

        Assert.Contains("Dismissed Patterns — Do Not Report", result, StringComparison.OrdinalIgnoreCase);
    }

    // T021 — Non-empty DismissedPatterns → output contains the actual pattern text
    [Fact]
    public void BuildSystemPrompt_WithDismissedPatterns_ContainsPatternText()
    {
        var context = new ReviewSystemContext(null, Array.Empty<RepositoryInstruction>(), null)
        {
            DismissedPatterns = new List<string> { "use idisposable pattern", "missing null check" }.AsReadOnly(),
        };

        var result = ReviewPrompts.BuildSystemPrompt(context);

        Assert.Contains("use idisposable pattern", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing null check", result, StringComparison.OrdinalIgnoreCase);
    }

    // T021 — Empty DismissedPatterns → exclusion section is absent
    [Fact]
    public void BuildSystemPrompt_WithEmptyDismissedPatterns_DoesNotContainDismissedSection()
    {
        var context = new ReviewSystemContext(null, Array.Empty<RepositoryInstruction>(), null)
        {
            DismissedPatterns = [],
        };

        var result = ReviewPrompts.BuildSystemPrompt(context);

        Assert.DoesNotContain("Dismissed Patterns", result, StringComparison.OrdinalIgnoreCase);
    }

    // T021 — Null context → dismissal section absent
    [Fact]
    public void BuildSystemPrompt_NullContext_DoesNotContainDismissedSection()
    {
        var result = ReviewPrompts.BuildSystemPrompt(null);

        Assert.DoesNotContain("Dismissed Patterns", result, StringComparison.OrdinalIgnoreCase);
    }
}
