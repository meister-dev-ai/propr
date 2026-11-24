// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Domain.Tests.ValueObjects;

public sealed class FindingPostDestinationMapTests
{
    [Fact]
    public void WithReviewOnlyTypes_WritingBackTheListThatWasRead_LeavesTheMapUnchanged()
    {
        var destinations = FindingPostDestinationMap.Normalize(
        [
            new KeyValuePair<string, FindingPostDestination>(FindingTypeTaxonomy.DocumentationTests, FindingPostDestination.ReviewOnly),
            new KeyValuePair<string, FindingPostDestination>(
                FindingPostDestinationMap.KeyFor(FindingTypeTaxonomy.DocumentationTests, CodeInsightFindingQualifier.Missing),
                FindingPostDestination.Summary),
            new KeyValuePair<string, FindingPostDestination>(FindingTypeTaxonomy.Security, FindingPostDestination.Summary),
        ]);

        var updated = FindingPostDestinationMap.WithReviewOnlyTypes(destinations, FindingPostDestinationMap.ReviewOnlyTypes(destinations));

        Assert.Equal(destinations, updated);
    }

    [Fact]
    public void WithReviewOnlyTypes_ATypeThatNewlyEntersTheList_LosesItsKeysWithAQualifier()
    {
        var missingKey = FindingPostDestinationMap.KeyFor(FindingTypeTaxonomy.DocumentationTests, CodeInsightFindingQualifier.Missing);
        var destinations = FindingPostDestinationMap.Normalize(
        [
            new KeyValuePair<string, FindingPostDestination>(missingKey, FindingPostDestination.Summary),
        ]);

        var updated = FindingPostDestinationMap.WithReviewOnlyTypes(destinations, [FindingTypeTaxonomy.DocumentationTests]);

        Assert.Equal(FindingPostDestination.ReviewOnly, Assert.Single(updated, entry => entry.Key == FindingTypeTaxonomy.DocumentationTests).Value);
        Assert.False(updated.ContainsKey(missingKey));
    }
}
