namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Type of change for a file in a pull request.
/// </summary>
public enum ChangeType
{
    /// <summary>Addition of a new file or content.</summary>
    Add,

    /// <summary>Modification of existing content.</summary>
    Edit,

    /// <summary>Removal of content.</summary>
    Delete,

    /// <summary>File was moved or renamed (may also include content edits).</summary>
    Rename,
}
