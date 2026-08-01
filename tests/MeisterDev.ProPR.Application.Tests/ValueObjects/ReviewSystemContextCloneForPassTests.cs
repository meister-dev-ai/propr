// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Tests.ValueObjects;

/// <summary>
///     A pass that runs under its own protocol copies the review context. The copy has to carry the review's
///     configuration, because a dropped per-client setting does not fail loudly: the pass runs with the default
///     and looks exactly like a client who never configured it.
/// </summary>
public sealed class ReviewSystemContextCloneForPassTests
{
    /// <summary>Members that describe the pass just run rather than the review, so a new pass starts clean.</summary>
    private static readonly string[] PerPassMembers = [nameof(ReviewSystemContext.ContextBudgetOutcome), nameof(ReviewSystemContext.ActiveLens)];

    [Fact]
    public void CloneForPass_CarriesEveryConfiguredSetting()
    {
        var source = new ReviewSystemContext("client message", [], null)
        {
            MaxContextTokens = 123_456,
            TokenizerName = "o200k_base",
            EnableEvidenceBackedVerification = true,
            EnableLanguageRobustScreening = true,
            EnableMultiPassUnion = true,
            MultiPassUnionPassCount = 3,
            IncludeLinkedItemsInContext = true,
            ReviewPasses = [new ReviewPassSpec(Guid.NewGuid(), "security")],
            Aggressiveness = ReviewAggressiveness.Assertive,
            OutputLanguage = "de",
        };

        var clone = source.CloneForPass();

        Assert.Equal(123_456, clone.MaxContextTokens);
        Assert.Equal("o200k_base", clone.TokenizerName);
        Assert.True(clone.EnableEvidenceBackedVerification);
        Assert.True(clone.EnableLanguageRobustScreening);
        Assert.True(clone.EnableMultiPassUnion);
        Assert.Equal(3, clone.MultiPassUnionPassCount);
        Assert.True(clone.IncludeLinkedItemsInContext);
        Assert.Same(source.ReviewPasses, clone.ReviewPasses);
        Assert.Equal("security", clone.ReviewPasses![0].Lens);
        Assert.Equal(ReviewAggressiveness.Assertive, clone.Aggressiveness);
        Assert.Equal("de", clone.OutputLanguage);
    }

    [Fact]
    public void CloneForPass_ResetsWhatDescribedThePreviousPass()
    {
        var source = new ReviewSystemContext(null, [], null)
        {
            ContextBudgetOutcome = ReviewContextBudgetOutcome.Skipped,
            ActiveLens = "security",
        };

        var clone = source.CloneForPass();

        Assert.Equal(ReviewContextBudgetOutcome.Normal, clone.ContextBudgetOutcome);
        Assert.Null(clone.ActiveLens);
    }

    [Fact]
    public void CloneForPass_LeavesTheOriginalAlone()
    {
        var source = new ReviewSystemContext(null, [], null) { ActiveLens = "security", OutputLanguage = "de" };

        var clone = source.CloneForPass();
        clone.OutputLanguage = "fr";

        Assert.Equal("security", source.ActiveLens);
        Assert.Equal("de", source.OutputLanguage);
    }

    /// <summary>
    ///     The guard that makes the original defect unrepeatable. A copy written out by hand drops whatever it
    ///     forgets, so this asserts no member is left behind, whatever is added to the class later.
    /// </summary>
    [Fact]
    public void CloneForPass_LeavesNoMemberBehind()
    {
        var source = new ReviewSystemContext("client message", [], null);
        var settable = typeof(ReviewSystemContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite && property.CanRead)
            .ToList();

        // Give every settable member a value that differs from its default, so a member the copy failed to
        // carry shows up as the default rather than silently matching.
        var assigned = settable
            .Where(property => TryAssignDistinctValue(source, property))
            .ToList();
        Assert.NotEmpty(assigned);

        var clone = source.CloneForPass();

        var dropped = assigned
            .Where(property => !PerPassMembers.Contains(property.Name))
            .Where(property => !Equals(property.GetValue(clone), property.GetValue(source)))
            .Select(property => property.Name)
            .ToList();

        Assert.Empty(dropped);
    }

    private static bool TryAssignDistinctValue(ReviewSystemContext target, PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var current = property.GetValue(target);

        object? value = type switch
        {
            not null when type == typeof(string) => "distinct-value",
            not null when type == typeof(bool) => !(current as bool? ?? false),
            not null when type == typeof(int) => (current as int? ?? 0) + 7,
            not null when type == typeof(float) => (current as float? ?? 0f) + 0.25f,
            not null when type == typeof(Guid) => Guid.NewGuid(),
            not null when type.IsEnum => Enum.GetValues(type).Cast<object>().FirstOrDefault(candidate => !Equals(candidate, current)),
            not null when type == typeof(IReadOnlyList<ReviewPassSpec>) => new[] { new ReviewPassSpec(Guid.NewGuid(), "distinct-lens") },
            _ => null,
        };

        if (value is null)
        {
            return false;
        }

        property.SetValue(target, value);
        return true;
    }
}
