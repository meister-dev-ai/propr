using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Summarises a single changed file within a pull request, including the type of change.
/// </summary>
/// <param name="Path">Repository-relative path of the changed file.</param>
/// <param name="ChangeType">The type of change applied to the file.</param>
public sealed record ChangedFileSummary(string Path, ChangeType ChangeType);
