// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.DataProtection;

/// <summary>
///     Configures the shared application name, key repository, and selected key-ring protector.
/// </summary>
/// <remarks>
///     ProPR and ProCursor encrypt and read the same stored secrets, so they have to share one protection
///     identity. Both hosts use the same configuration for the application name, key path, and protector.
/// </remarks>
public static class MeisterDataProtectionExtensions
{
    /// <summary>The application name both hosts derive their protection identity from.</summary>
    public const string ApplicationName = "MeisterProPR";

    /// <summary>The variable naming the directory the key ring is stored in.</summary>
    public const string KeysPathKey = "MEISTER_DATA_PROTECTION_KEYS_PATH";

    /// <summary>The variable naming the key-ring protector.</summary>
    public const string ProtectorKey = "MEISTER_DATA_PROTECTION_PROTECTOR";

    /// <summary>The variable naming the directory protector add-ins are read from.</summary>
    public const string AddInDirectoryKey = "MEISTER_DATA_PROTECTION_ADD_IN_DIRECTORY";

    /// <summary>The protector name that leaves the key files unencrypted, and the default.</summary>
    public const string NoProtectorName = "none";

    /// <summary>The add-in directory used when the variable is absent, beside the host's own assemblies.</summary>
    public const string DefaultAddInDirectoryName = "data-protection-add-ins";

    /// <summary>Adds the shared data-protection set-up to <paramref name="services" />.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The configuration the host was started with.</param>
    /// <returns>The builder, so a host can add to it.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The selected protector is provided by nothing, or a protector is missing a setting it needs. Either
    ///     fails start-up, because a host that came up with a key ring protected differently from the one the
    ///     operator asked for would write keys the intended protector cannot read.
    /// </exception>
    public static IDataProtectionBuilder AddMeisterDataProtection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);

        var keysPath = ResolveKeysPath(configuration);
        if (keysPath is not null)
        {
            Directory.CreateDirectory(keysPath);
            builder.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }

        var selected = configuration[ProtectorKey];
        if (string.IsNullOrWhiteSpace(selected)
            || string.Equals(selected.Trim(), NoProtectorName, StringComparison.OrdinalIgnoreCase))
        {
            return builder;
        }

        var addInDirectory = ResolveAddInDirectory(configuration);
        var protector = KeyRingProtectorCatalog.Find(selected.Trim(), addInDirectory)
                        ?? throw new InvalidOperationException(
                            $"{ProtectorKey} names the key-ring protector '{selected.Trim()}', which is neither "
                            + $"built in nor supplied by an add-in in '{addInDirectory}'. The built-in names are "
                            + $"'{NoProtectorName}' and "
                            + $"{string.Join(", ", KeyRingProtectorCatalog.BuiltIn.Select(candidate => $"'{candidate.Name}'"))}.");

        protector.Apply(builder, configuration);

        return builder;
    }

    /// <summary>
    ///     The configured key directory, or <see langword="null" /> where no
    ///     directory is configured and the framework's own key repository stays in use.
    /// </summary>
    /// <param name="configuration">The configuration the host was started with.</param>
    /// <remarks>
    ///     Relative paths resolve against the process working directory. The configured value is preserved,
    ///     including whitespace, so upgrades continue using the existing key repository.
    /// </remarks>
    internal static string? ResolveKeysPath(IConfiguration configuration)
    {
        var configured = configuration[KeysPathKey];

        return string.IsNullOrWhiteSpace(configured)
            ? null
            : configured;
    }

    /// <summary>The directory protector add-ins are read from, as an absolute path.</summary>
    /// <param name="configuration">The configuration the host was started with.</param>
    internal static string ResolveAddInDirectory(IConfiguration configuration)
    {
        var configured = configuration[AddInDirectoryKey];

        return string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, DefaultAddInDirectoryName))
            : Path.GetFullPath(configured.Trim(), AppContext.BaseDirectory);
    }
}
