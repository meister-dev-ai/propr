// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.CodeInsights.Tests;

/// <summary>
///     Runs allocation measurements without concurrent test collections because the allocation counter covers the whole process.
/// </summary>
[CollectionDefinition(nameof(ReviewerPerformanceAllocationCollection), DisableParallelization = true)]
public sealed class ReviewerPerformanceAllocationCollection : ICollectionFixture<PostgresContainerFixture>
{
}
