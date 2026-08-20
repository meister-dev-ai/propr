// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Tests.Fixtures;

/// <summary>
///     Groups the tests that run git as a child process, so they do not run at the same time as each other.
/// </summary>
/// <remarks>
///     One of them sets <c>GIT_DIR</c> on the test process, because a child inheriting it is the condition
///     under test. While it is set, any git command started by another test would inherit it too. Collecting
///     the tests that start git serialises them against that one.
/// </remarks>
[CollectionDefinition("GitCommandLine")]
public sealed class GitCommandLineCollection
{
    // Marker class, no members needed.
}
