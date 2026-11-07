// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;

namespace MeisterDev.ProPR.TestSupport;

/// <summary>
///     Identifies the test process that started a container, and removes the containers of test processes that
///     have ended.
/// </summary>
/// <remarks>
///     <para>
///         A fixture stops and removes its container when it is disposed. A test process that is killed disposes
///         nothing. With the Testcontainers resource reaper disabled, its container keeps running with its
///         PostgreSQL server processes until someone removes it. A fixture therefore removes such containers
///         before it starts its own.
///     </para>
///     <para>
///         The identity consists of the machine name, the boot id, the process namespace, the process id and the
///         start time of the process in clock ticks since boot, as Linux reports them in <c>/proc</c>. A fixture
///         removes a container only when the machine name and the process namespace in its label are those of
///         the current process and the process in the label no longer runs: the machine was restarted since, no
///         process with that id exists, or the process with that id started at another time. On other operating
///         systems a fixture removes no containers.
///     </para>
/// </remarks>
public static class TestContainerOwner
{
    /// <summary>The label that carries the identity of the test process that started a container.</summary>
    public const string Label = "meisterdev.propr.tests.owner";

    /// <summary>The label value of the current process.</summary>
    public static string Current { get; } = string.Join(
        '|',
        Environment.MachineName,
        ReadBootId(),
        ReadProcessNamespace(),
        Environment.ProcessId,
        ReadStartTime(Environment.ProcessId));

    /// <summary>
    ///     Removes every labelled container whose test process has ended. Failures are ignored, because a test
    ///     run must not fail over the leftovers of an earlier run.
    /// </summary>
    /// <param name="dockerEndpoint">
    ///     The endpoint the fixture passes to Testcontainers, or <see langword="null" /> when Testcontainers
    ///     detects the endpoint itself.
    /// </param>
    public static async Task RemoveContainersOfEndedProcessesAsync(string? dockerEndpoint)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            var endpoint = dockerEndpoint is null
                ? TestcontainersSettings.OS.DockerEndpointAuthConfig
                : new DockerEndpointAuthenticationConfiguration(new Uri(dockerEndpoint));

            using var client = endpoint.GetDockerClientBuilder().Build();
            var containers = await client.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["label"] = new Dictionary<string, bool> { [Label] = true },
                    },
                });

            foreach (var container in containers)
            {
                if (container.Labels is not null && container.Labels.TryGetValue(Label, out var owner) && HasEnded(owner))
                {
                    await RemoveAsync(client, container.ID);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // No Docker endpoint, or one that refused the request. The fixture reports an unavailable endpoint
            // when it starts its own container.
        }
    }

    private static async Task RemoveAsync(IDockerClient client, string containerId)
    {
        try
        {
            await client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });
        }
        catch (DockerApiException)
        {
            // Another fixture removed the container first.
        }
    }

    private static bool HasEnded(string owner)
    {
        var parts = owner.Split('|');
        if (parts.Length != 5
            || parts[0] != Environment.MachineName
            || parts[2] != ReadProcessNamespace()
            || !int.TryParse(parts[3], out var processId)
            || processId == Environment.ProcessId)
        {
            return false;
        }

        return parts[1] != ReadBootId() || ReadStartTime(processId) != parts[4];
    }

    private static string ReadBootId()
    {
        return ReadOrEmpty(() => File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim());
    }

    private static string ReadProcessNamespace()
    {
        return ReadOrEmpty(() => new FileInfo("/proc/self/ns/pid").LinkTarget ?? string.Empty);
    }

    // The start time is the 22nd field of /proc/<pid>/stat. The second field, the command name, is enclosed in
    // parentheses and may itself hold spaces and parentheses, so the fields are counted from the last ')'.
    private static string ReadStartTime(int processId)
    {
        return ReadOrEmpty(() =>
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return fields[19];
        });
    }

    private static string ReadOrEmpty(Func<string> read)
    {
        if (!OperatingSystem.IsLinux())
        {
            return string.Empty;
        }

        try
        {
            return read();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or IndexOutOfRangeException
                                              or ArgumentOutOfRangeException)
        {
            return string.Empty;
        }
    }
}
