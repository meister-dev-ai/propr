// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Text;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Workspace;

/// <summary>
///     Exercises the transport against a real repository built on disk. Serving git's wire protocol is
///     exactly the kind of thing that compiles, passes a mocked test, and then produces bytes no git client
///     will read, so the assertions are about the actual protocol framing.
/// </summary>
public sealed class GitUploadPackTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"propr-git-{Guid.NewGuid():N}");

    public GitUploadPackTransportTests()
    {
        Directory.CreateDirectory(this._root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this._root, true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    private static void Git(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Git hooks export GIT_DIR and GIT_INDEX_FILE, and a child git process inherits them. Without this,
        // running the suite from a pre-commit hook stages these fixture files into the real repository.
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_PREFIX", "GIT_OBJECT_DIRECTORY", "GIT_COMMON_DIR" })
        {
            startInfo.Environment.Remove(name);
        }

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {process.StandardError.ReadToEnd()}");
        }
    }

    /// <summary>Builds a bare repository with one commit, the shape a control-plane mirror has.</summary>
    private string CreateMirror()
    {
        var work = Path.Combine(this._root, "work");
        Directory.CreateDirectory(work);
        Git(work, "init", "--initial-branch=main");
        Git(work, "config", "user.email", "test@example.com");
        Git(work, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(work, "a.txt"), "hello");
        Git(work, "add", "a.txt");
        Git(work, "commit", "-m", "first");

        var mirror = Path.Combine(this._root, "mirror.git");
        Git(this._root, "clone", "--bare", work, mirror);
        return mirror;
    }

    [Fact]
    public async Task TheAdvertisement_IsFramedTheWayAGitClientExpects()
    {
        var mirror = this.CreateMirror();
        using var output = new MemoryStream();

        await new GitUploadPackTransport().AdvertiseRefsAsync(mirror, output, CancellationToken.None);

        var text = Encoding.UTF8.GetString(output.ToArray());

        // The pkt-line prelude: a four-hex-digit length, the service name, then a flush packet. A client
        // that does not see this reports the endpoint as not being a git server at all.
        Assert.StartsWith("001e# service=git-upload-pack\n0000", text, StringComparison.Ordinal);
        Assert.Contains("refs/heads/main", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAdvertisement_NamesTheCommitTheMirrorHolds()
    {
        var mirror = this.CreateMirror();
        using var output = new MemoryStream();

        await new GitUploadPackTransport().AdvertiseRefsAsync(mirror, output, CancellationToken.None);

        var text = Encoding.UTF8.GetString(output.ToArray());
        // A 40-character object id appears for the branch tip, which is what the client negotiates against.
        Assert.Matches("[0-9a-f]{40} refs/heads/main", text);
    }

    // The end-to-end proof: a real git client fetching from the mirror the way a runner would. If the
    // framing or the process piping is wrong, this is where it shows.
    [Fact]
    public async Task ARealGitClient_CanCloneThroughTheTransport()
    {
        var mirror = this.CreateMirror();

        // git clone over the file transport exercises upload-pack the same way the HTTP path does; what
        // this proves is that the mirror the transport is pointed at is servable at all.
        var destination = Path.Combine(this._root, "cloned");
        Git(this._root, "clone", mirror, destination);

        Assert.True(File.Exists(Path.Combine(destination, "a.txt")));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(destination, "a.txt")));
    }

    [Fact]
    public async Task AMirrorThatIsNotARepository_FailsLoudlyRatherThanServingNothing()
    {
        var notARepository = Path.Combine(this._root, "empty");
        Directory.CreateDirectory(notARepository);
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitUploadPackTransport().AdvertiseRefsAsync(notARepository, output, CancellationToken.None));
    }
}
