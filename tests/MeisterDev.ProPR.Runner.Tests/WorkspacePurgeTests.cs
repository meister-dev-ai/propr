// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Runner.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     What a runner host must not keep. A review's working copy is a customer's source on a machine that
///     exists to be disposable and may be imaged, recycled, or shared. This host is treated as untrusted
///     storage because the code it reads does not belong to it.
///     <para>
///         The rest of what a review produces never touches this disk: its trace, its per-file outcomes and
///         its spend all go through the spool, which is a memory buffer sent to the control plane. The working
///         copy is the only thing to remove.
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

    // A host that stopped mid-review left a customer's source on disk. A restarted runner removes it before
    // it asks for any new work that would add more.
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

    /// <summary>
    ///     The startup sweep runs before the host enrols. A work root it cannot read used to end the process
    ///     there, and an orchestrator restarted the container into the same failure without an operator ever
    ///     seeing the reason.
    /// </summary>
    [Fact]
    public void APurgeOnAWorkRootThisHostCannotRead_IsReportedAndNotThrown()
    {
        // A file mode decides who may list a directory on Linux, and a privileged process may list one
        // whatever its mode says. Elsewhere this case cannot be arranged, and the test asserts nothing.
        if (!OperatingSystem.IsLinux() || Environment.IsPrivilegedProcess)
        {
            return;
        }

        this.GiveJobAWorkingCopy(Guid.NewGuid());
        File.SetUnixFileMode(this._root, UnixFileMode.None);

        try
        {
            this.CreateFetcher().Purge();
        }
        finally
        {
            File.SetUnixFileMode(this._root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
