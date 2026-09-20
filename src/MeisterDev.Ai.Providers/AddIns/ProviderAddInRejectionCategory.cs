// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.AddIns;

/// <summary>Why the loader did not take an assembly it found in an add-in directory.</summary>
/// <remarks>
///     A closed set, so an operator reading the inventory sorts the causes apart without reading a message, and
///     because the remedy differs per category. The reason recorded beside it carries the detail.
/// </remarks>
public enum ProviderAddInRejectionCategory
{
    /// <summary>The assembly threw while it was being read, or exposed no provider family.</summary>
    Failed = 0,

    /// <summary>Another assembly, or a family compiled into the host, already claimed the same identity.</summary>
    Duplicate = 1,

    /// <summary>The folder holds a copy of an assembly the host and the add-in have to share.</summary>
    MisPackaged = 2,

    /// <summary>The family declared a contract version this host does not accept.</summary>
    VersionMismatch = 3,

    /// <summary>The family loaded and then failed one of the shared driver checks.</summary>
    NonConforming = 4,
}
