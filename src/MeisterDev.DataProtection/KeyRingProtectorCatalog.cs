// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;

namespace MeisterDev.DataProtection;

/// <summary>Every key-ring protector this installation can select, compiled in or supplied by an add-in.</summary>
internal static class KeyRingProtectorCatalog
{
    /// <summary>The protectors compiled into this assembly.</summary>
    internal static IReadOnlyList<IKeyRingProtector> BuiltIn { get; } = [new CertificateKeyRingProtector()];

    /// <summary>
    ///     The protector named <paramref name="name" />, or <see langword="null" /> when nothing provides it.
    /// </summary>
    /// <param name="name">The selected protector name.</param>
    /// <param name="addInDirectory">The directory protector add-ins are read from.</param>
    /// <remarks>
    ///     A compiled-in protector answers first, so a directory an operator can write to cannot take over a
    ///     name the product ships, and the directory is not read at all for a name the product provides. An
    ///     assembly that cannot be read, a type that cannot be created and a name that cannot be asked for are
    ///     each passed over: one unusable file in a mounted directory must not stop a host whose protector sits
    ///     in another file.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     Two add-ins provide the same name, or two types of one add-in do. Which of them a host would run is
    ///     then decided by where the files sit or by the order reflection reports the types in, so start-up
    ///     stops with both named instead.
    /// </exception>
    public static IKeyRingProtector? Find(string name, string addInDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var built = BuiltIn.FirstOrDefault(protector => Names(protector, name));
        if (built is not null)
        {
            return built;
        }

        if (!Directory.Exists(addInDirectory))
        {
            return null;
        }

        IKeyRingProtector? found = null;
        var foundIn = string.Empty;

        // Every candidate is read, not only candidates up to the first answer, so two add-ins claiming one name
        // are reported instead of decided by enumeration order.
        foreach (var assemblyPath in EnumerateAddInAssemblies(addInDirectory))
        {
            var candidate = TryFindIn(assemblyPath, name);
            if (candidate is null)
            {
                continue;
            }

            if (found is not null)
            {
                throw new InvalidOperationException(
                    $"The key-ring protector '{name}' is supplied by two add-ins, '{foundIn}' and "
                    + $"'{assemblyPath}'. Remove one of them from the add-in directory.");
            }

            found = candidate;
            foundIn = assemblyPath;
        }

        return found;
    }

    /// <summary>The add-in assemblies under <paramref name="addInDirectory" />, in a fixed order.</summary>
    /// <param name="addInDirectory">The directory protector add-ins are read from.</param>
    /// <remarks>
    ///     The order is the same on every host and every filesystem, so a directory holding one protector
    ///     resolves to the same file everywhere. A directory that cannot be listed is passed over on its own:
    ///     an unreadable subdirectory beside the add-in must not decide whether the add-in is found. The files
    ///     of a directory and its subdirectories are read in separate guarded steps for the same reason, so a
    ///     file that disappears mid-scan costs that one listing and not the subtree below it.
    /// </remarks>
    private static IReadOnlyList<string> EnumerateAddInAssemblies(string addInDirectory)
    {
        var root = Path.GetFullPath(addInDirectory);
        var assemblyPaths = new List<string>();
        var pending = new Queue<string>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            assemblyPaths.AddRange(AssembliesIn(directory));
            foreach (var child in ScannedChildrenOf(directory, root))
            {
                pending.Enqueue(child);
            }
        }

        assemblyPaths.Sort(StringComparer.Ordinal);

        return assemblyPaths;
    }

    /// <summary>The assembly files directly in <paramref name="directory" />.</summary>
    /// <param name="directory">One directory of the scan.</param>
    /// <remarks>
    ///     The extension is matched without regard to case, so a file named <c>Protector.DLL</c> is an add-in
    ///     on every host: a filesystem pattern answers that question differently on Windows and on Linux, and
    ///     an operator's directory would then hold an add-in on one host and nothing on the other.
    ///     <para>
    ///         A file link is passed over. It names an assembly wherever whoever created it chose, and loading
    ///         it would run code from a file outside the directory the operator configured. The lexical check
    ///         the subdirectories are held to says nothing about a file: the path stays inside the root and
    ///         the bytes come from elsewhere.
    ///     </para>
    /// </remarks>
    private static IReadOnlyList<string> AssembliesIn(string directory)
    {
        try
        {
            return
            [
                .. Directory.EnumerateFiles(directory)
                    .Where(path => Path.GetExtension(path.AsSpan()).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    .Where(path => IsNotALink(new FileInfo(path))),
            ];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A directory deleted or made unreadable while the scan runs, or a file that vanished as it was
            // being listed. Neither says anything about the directories below it.
            return [];
        }
    }

    /// <summary>
    ///     Whether the filesystem reports <paramref name="entry" /> as an ordinary file or directory and not
    ///     as a link.
    /// </summary>
    /// <param name="entry">One child of a directory the scan is reading.</param>
    /// <remarks>
    ///     An entry whose attributes cannot be read answers the same way a link does, so an unreadable child
    ///     is passed over instead of failing the listing it sits in. Each entry is asked on its own for that
    ///     reason: one child that disappears or refuses access must not cost the scan its siblings.
    ///     <para>
    ///         The answer describes the entry at the moment it was read. An entry can be replaced with a link
    ///         after that and before the file is loaded or the directory is enumerated. .NET exposes no call
    ///         that opens the entry and reads or lists that same handle, so the window cannot be closed here.
    ///         The add-in directory belongs to the operator and the image fixes its path, so an account that
    ///         can replace an entry in it can replace the add-in assembly itself.
    ///     </para>
    /// </remarks>
    private static bool IsNotALink(FileSystemInfo entry)
    {
        try
        {
            return !entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     The subdirectories of <paramref name="directory" /> the scan continues into.
    /// </summary>
    /// <param name="directory">One directory of the scan.</param>
    /// <param name="root">The configured add-in directory, which the scan stays inside.</param>
    /// <remarks>
    ///     A directory link below the add-in directory points wherever whoever created it chose, so following
    ///     one would load assemblies from a location the operator never configured as the add-in directory.
    ///     A link is passed over, and the resolved path of every remaining child has to stay under the root as
    ///     well, so nothing the filesystem reports as an ordinary directory takes the scan out either.
    /// </remarks>
    private static IReadOnlyList<string> ScannedChildrenOf(string directory, string root)
    {
        try
        {
            return
            [
                .. Directory.EnumerateDirectories(directory)
                    .Select(child => new DirectoryInfo(child))
                    .Where(child => IsNotALink(child) && StaysUnder(root, child.FullName))
                    .Select(child => child.FullName),
            ];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Whether <paramref name="path" /> leads to somewhere inside <paramref name="root" />.</summary>
    /// <param name="root">The configured add-in directory.</param>
    /// <param name="path">A path the filesystem reported below it.</param>
    private static bool StaysUnder(string root, string path)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));

        return !Path.IsPathRooted(relative)
               && !relative.Equals("..", StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The protector <paramref name="assemblyPath" /> provides under <paramref name="name" />, or
    ///     <see langword="null" /> when it provides none.
    /// </summary>
    /// <param name="assemblyPath">One candidate file of the add-in directory.</param>
    /// <param name="name">The selected protector name.</param>
    /// <remarks>
    ///     Every type of the assembly is asked, not types up to the first answer, for the same reason every
    ///     file of the directory is read: two types answering to one name would otherwise be decided by the
    ///     order reflection reports them in.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     Two types of this assembly answer to the name. Both are named so whoever built the add-in can
    ///     remove one.
    /// </exception>
    private static IKeyRingProtector? TryFindIn(string assemblyPath, string name)
    {
        Type?[] types;
        try
        {
            types = new KeyRingProtectorAddInLoadContext(assemblyPath)
                .LoadFromAssemblyPath(assemblyPath)
                .GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            // Some of the assembly's types failed to load, which the ones that did are unaffected by, so the
            // protector is still looked for among them.
            types = exception.Types;
        }
        catch (Exception exception) when (exception is BadImageFormatException
                                              or FileLoadException
                                              or FileNotFoundException
                                              or IOException
                                              or UnauthorizedAccessException)
        {
            return null;
        }

        IKeyRingProtector? found = null;
        Type? foundType = null;

        foreach (var type in types)
        {
            if (type is null
                || !typeof(IKeyRingProtector).IsAssignableFrom(type)
                || type.IsAbstract
                || type.IsInterface
                || type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            if (TryMatch(type, name) is not { } protector)
            {
                continue;
            }

            if (found is not null)
            {
                throw new InvalidOperationException(
                    $"The key-ring protector '{name}' is supplied by two types of the add-in "
                    + $"'{assemblyPath}', '{foundType!.FullName}' and '{type.FullName}'. Remove one of them.");
            }

            found = protector;
            foundType = type;
        }

        return found;
    }

    /// <summary>
    ///     The protector <paramref name="type" /> creates when it answers to <paramref name="name" />, or
    ///     <see langword="null" /> when it does not or cannot be asked.
    /// </summary>
    /// <param name="type">A type implementing the contract.</param>
    /// <param name="name">The selected protector name.</param>
    /// <remarks>
    ///     Creating the type and reading its name both run the add-in's own code, and either can throw for
    ///     reasons that say nothing about the other candidates: a dependency the add-in did not ship, a
    ///     constructor reading configuration it does not have. The failure is contained to this type so the
    ///     scan continues with the next one, which is the behaviour a mounted directory of third-party files
    ///     needs.
    /// </remarks>
    private static IKeyRingProtector? TryMatch(Type type, string name)
    {
        try
        {
            return Activator.CreateInstance(type) is IKeyRingProtector protector && Names(protector, name)
                ? protector
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Names(IKeyRingProtector protector, string name)
    {
        return string.Equals(protector.Name, name, StringComparison.OrdinalIgnoreCase);
    }
}
