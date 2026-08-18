// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Reports the version of the running product build.</summary>
public interface IProductVersionProvider
{
    /// <summary>
    ///     The running release version, or a placeholder when the build was not stamped.
    ///     <para>
    ///         Release images stamp the git tag into the assembly. A developer build has no tag to stamp, so
    ///         the value is a recognisable placeholder rather than a version-shaped number.
    ///     </para>
    /// </summary>
    string Version { get; }
}
