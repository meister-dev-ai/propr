// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Runner.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     What a runner host must not keep. A review's working copy is a customer's source on a machine that
///     exists to be disposable and may be imaged, recycled, or shared; the whole reason this host is
///     treated as untrusted storage is that the code it reads does not belong to it.
///     <para>
///         The rest of what a review produces — its trace, its per-file outcomes, its spend — never touches
///         this disk at all: the spool is a memory buffer that ships to the control plane. The working copy
///         is the only thing there is to remove.
///     </para>
/// </summary>
public sealed class WorkspacePurgeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"propr-runner-purge-{Guid.NewGuid():N}");

    [Fact]
    public void AFinishedJob_LeavesNothingOfItsOwnBehind()
    {
        var job = Guid.NewGuid();
        var other = Guid.NewGuid();
        this.GiveJobAWorkingCopy(job);
        this.GiveJobAWorkingCopy(other);

        this.CreateFetcher().Purge(job);

        Assert.False(Directory.Exists(Path.Combine(this._root, job.ToString("D"))));

        // Scoped to the job that ended. A purge that took the whole root would delete the working copy of
        // every other job this runner is reviewing at the same time.
        Assert.True(Directory.Exists(Path.Combine(this._root, other.ToString("D"))));
    }

    // A host that died mid-review left somebody's source on disk. The first thing a restarted runner does
    // is get rid of it, before it asks for anything new to add to it.
    [Fact]
    public void AStartupSweep_RemovesWhateverAPreviousLifeLeft()
    {
        this.GiveJobAWorkingCopy(Guid.NewGuid());
        this.GiveJobAWorkingCopy(Guid.NewGuid());

        this.CreateFetcher().Purge();

        Assert.Empty(Directory.GetDirectories(this._root));
    }

    [Fact]
    public void APurgeOnAHostThatHasReviewedNothing_IsNotAnError()
    {
        this.CreateFetcher().Purge();
        this.CreateFetcher().Purge(Guid.NewGuid());
    }

    public void Dispose()
    {
        if (Directory.Exists(this._root))
        {
            Directory.Delete(this._root, recursive: true);
        }
    }

    /// <summary>A job directory shaped like a real one: a bare mirror and the two checked-out revisions.</summary>
    private void GiveJobAWorkingCopy(Guid jobId)
    {
        var job = Path.Combine(this._root, jobId.ToString("D"));
        foreach (var part in new[] { "mirror", "source", "target" })
        {
            Directory.CreateDirectory(Path.Combine(job, part));
            File.WriteAllText(Path.Combine(job, part, "Secret.cs"), "// a customer's source");
        }
    }

    private WorkspaceFetcher CreateFetcher()
    {
        return new WorkspaceFetcher(
            Options.Create(new RunnerHostOptions { ControlPlaneUrl = "https://control-plane.invalid", WorkRootPath = this._root }),
            NullLogger<WorkspaceFetcher>.Instance);
    }
}
