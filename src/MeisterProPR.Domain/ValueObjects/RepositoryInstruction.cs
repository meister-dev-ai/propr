namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Represents a repository-specific instruction file that guides the AI reviewer.
///     Instruction files use a triple-quote header block to declare metadata.
/// </summary>
/// <param name="FileName">Name of the instruction file within the repository.</param>
/// <param name="Description">Short description of what this instruction covers.</param>
/// <param name="WhenToUse">Guidance on when the AI reviewer should apply this instruction.</param>
/// <param name="Body">Full text content of the instruction file, including the header.</param>
public sealed record RepositoryInstruction(string FileName, string Description, string WhenToUse, string Body)
{
    /// <summary>
    ///     Parses a <see cref="RepositoryInstruction" /> from raw file content.
    ///     Returns <see langword="null" /> if the header block is absent or if
    ///     <c>description</c> or <c>when-to-use</c> are empty.
    /// </summary>
    /// <param name="fileName">Name of the file being parsed.</param>
    /// <param name="content">Full text content of the instruction file.</param>
    /// <returns>A parsed <see cref="RepositoryInstruction" />, or <see langword="null" /> if the header is invalid.</returns>
    public static RepositoryInstruction? Parse(string fileName, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        const string headerOpen = "\"\"\"";
        const string headerClose = "\"\"\"";

        var openIndex = content.IndexOf(headerOpen, StringComparison.Ordinal);
        if (openIndex < 0)
        {
            return null;
        }

        var closeIndex = content.IndexOf(headerClose, openIndex + headerOpen.Length, StringComparison.Ordinal);
        if (closeIndex < 0)
        {
            return null;
        }

        var headerBlock = content[(openIndex + headerOpen.Length)..closeIndex];
        var description = ExtractField(headerBlock, "description:");
        var whenToUse = ExtractField(headerBlock, "when-to-use:");

        if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(whenToUse))
        {
            return null;
        }

        return new RepositoryInstruction(fileName, description, whenToUse, content);
    }

    private static string? ExtractField(string block, string prefix)
    {
        foreach (var line in block.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[prefix.Length..].Trim();
            }
        }

        return null;
    }
}
