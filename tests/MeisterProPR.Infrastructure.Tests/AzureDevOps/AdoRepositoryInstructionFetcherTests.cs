using Azure.Core;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.AzureDevOps;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

/// <summary>
///     Unit tests for <see cref="AdoRepositoryInstructionFetcher" /> using a testable subclass
///     that bypasses the ADO network layer.
/// </summary>
public class AdoRepositoryInstructionFetcherTests
{
    private const string ValidHeader = """
                                       \"\"\"
                                       description: Enforce naming conventions
                                       when-to-use: When reviewing any C# file
                                       \"\"\"
                                       """;

    private static string BuildInstructionContent(string description, string whenToUse, string body = "Apply naming rules.")
    {
        return $"\"\"\"\ndescription: {description}\nwhen-to-use: {whenToUse}\n\"\"\"\n{body}";
    }

    [Fact]
    public async Task FetchAsync_ValidHeaderFile_ParsedIntoRepositoryInstruction()
    {
        // Arrange
        var sut = new TestableAdoRepositoryInstructionFetcher();
        sut.AddFile("instructions-naming.md", BuildInstructionContent("Enforce naming conventions", "When reviewing C# files"));

        // Act
        var result = await sut.FetchAsync("https://dev.azure.com/org", "proj", "repo", "main", null, CancellationToken.None);

        // Assert
        Assert.Single(result);
        Assert.Equal("instructions-naming.md", result[0].FileName);
        Assert.Equal("Enforce naming conventions", result[0].Description);
        Assert.Equal("When reviewing C# files", result[0].WhenToUse);
    }

    [Fact]
    public async Task FetchAsync_FileWithoutValidHeader_SilentlyIgnored()
    {
        // Arrange — file has no """...""" header block
        var sut = new TestableAdoRepositoryInstructionFetcher();
        sut.AddFile("instructions-no-header.md", "This file has no header at all. Just some text.");

        // Act
        var result = await sut.FetchAsync("https://dev.azure.com/org", "proj", "repo", "main", null, CancellationToken.None);

        // Assert — silently ignored, returns empty list
        Assert.Empty(result);
    }

    [Fact]
    public async Task FetchAsync_EmptyFolder_ReturnsEmptyListWithoutError()
    {
        // Arrange — folder exists but contains no files
        var sut = new TestableAdoRepositoryInstructionFetcher();
        // No files added — _files is empty

        // Act
        var result = await sut.FetchAsync("https://dev.azure.com/org", "proj", "repo", "main", null, CancellationToken.None);

        // Assert — no exception, empty result
        Assert.Empty(result);
    }

    [Fact]
    public async Task FetchAsync_AbsentFolder_ReturnsEmptyListWithoutError()
    {
        // Arrange — .meister-propr/ folder does not exist
        var sut = new TestableAdoRepositoryInstructionFetcher();
        sut.SetFolderAbsent();

        // Act
        var result = await sut.FetchAsync("https://dev.azure.com/org", "proj", "repo", "main", null, CancellationToken.None);

        // Assert — graceful, returns empty list
        Assert.Empty(result);
    }

    [Fact]
    public async Task FetchAsync_MultipleFiles_ReturnedInAlphabeticalOrder()
    {
        // Arrange — add files out of alphabetical order
        var sut = new TestableAdoRepositoryInstructionFetcher();
        sut.AddFile("instructions-zz-last.md", BuildInstructionContent("ZZ last", "Always"));
        sut.AddFile("instructions-aa-first.md", BuildInstructionContent("AA first", "Always"));
        sut.AddFile("instructions-mm-middle.md", BuildInstructionContent("MM middle", "Always"));

        // Act
        var result = await sut.FetchAsync("https://dev.azure.com/org", "proj", "repo", "main", null, CancellationToken.None);

        // Assert — alphabetical by file name
        Assert.Equal(3, result.Count);
        Assert.Equal("instructions-aa-first.md", result[0].FileName);
        Assert.Equal("instructions-mm-middle.md", result[1].FileName);
        Assert.Equal("instructions-zz-last.md", result[2].FileName);
    }

    [Fact]
    public async Task FetchAsync_MixedValidAndInvalidFiles_OnlyValidOnesReturned()
    {
        // Arrange
        var sut = new TestableAdoRepositoryInstructionFetcher();
        sut.AddFile("instructions-valid.md", BuildInstructionContent("Valid instruction", "When needed"));
        sut.AddFile("instructions-no-description.md", "\"\"\"\nwhen-to-use: Always\n\"\"\"\nsome body");
        sut.AddFile("instructions-empty.md", "");

        // Act
        var result = await sut.FetchAsync("https://dev.azure.com/org", "proj", "repo", "main", null, CancellationToken.None);

        // Assert — only the valid file returned
        Assert.Single(result);
        Assert.Equal("instructions-valid.md", result[0].FileName);
    }

    /// <summary>
    ///     Testable subclass that replaces ADO calls with in-memory file lists.
    /// </summary>
    private sealed class TestableAdoRepositoryInstructionFetcher : AdoRepositoryInstructionFetcher
    {
        private readonly List<(string fileName, string content)> _files = [];
        private bool _folderExists = true;

        public TestableAdoRepositoryInstructionFetcher()
            : base(
                new VssConnectionFactory(Substitute.For<TokenCredential>()),
                Substitute.For<IClientAdoCredentialRepository>(),
                Substitute.For<ILogger<AdoRepositoryInstructionFetcher>>())
        {
        }

        /// <summary>Registers a file with its content for the virtual .meister-propr/ folder.</summary>
        public void AddFile(string fileName, string content)
        {
            this._files.Add((fileName, content));
        }

        /// <summary>Simulates an absent .meister-propr/ folder.</summary>
        public void SetFolderAbsent()
        {
            this._folderExists = false;
        }

        /// <inheritdoc />
        protected override Task<IReadOnlyList<(string FileName, string Content)>?> FetchInstructionFilesAsync(
            string organizationUrl,
            string projectId,
            string repositoryId,
            string targetBranch,
            Guid? clientId,
            CancellationToken cancellationToken)
        {
            if (!this._folderExists)
            {
                return Task.FromResult<IReadOnlyList<(string FileName, string Content)>?>(null);
            }

            return Task.FromResult<IReadOnlyList<(string FileName, string Content)>?>(this._files.Select(f => (f.fileName, f.content)).ToList().AsReadOnly());
        }
    }
}
