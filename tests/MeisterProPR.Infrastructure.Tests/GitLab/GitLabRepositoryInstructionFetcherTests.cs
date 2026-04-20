// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.GitLab;

public sealed class GitLabRepositoryInstructionFetcherTests
{
    private static string BuildInstructionContent(
        string description,
        string whenToUse,
        string body = "Apply naming rules.")
    {
        return $"\"\"\"\ndescription: {description}\nwhen-to-use: {whenToUse}\n\"\"\"\n{body}";
    }

    [Fact]
    public async Task FetchAsync_ValidHeaderFile_ParsedIntoRepositoryInstruction()
    {
        var sut = new TestableGitLabRepositoryInstructionFetcher();
        sut.AddFile(
            "instructions-naming.md",
            BuildInstructionContent("Enforce naming conventions", "When reviewing C# files"));

        var result = await sut.FetchAsync(
            "https://gitlab.example.com",
            "acme",
            "repo",
            "main",
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("instructions-naming.md", result[0].FileName);
        Assert.Equal("Enforce naming conventions", result[0].Description);
        Assert.Equal("When reviewing C# files", result[0].WhenToUse);
    }

    [Fact]
    public async Task FetchAsync_AbsentFolder_ReturnsEmptyListWithoutError()
    {
        var sut = new TestableGitLabRepositoryInstructionFetcher();
        sut.SetFolderAbsent();

        var result = await sut.FetchAsync(
            "https://gitlab.example.com",
            "acme",
            "repo",
            "main",
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FetchAsync_MultipleFiles_ReturnedInAlphabeticalOrder()
    {
        var sut = new TestableGitLabRepositoryInstructionFetcher();
        sut.AddFile("instructions-zz-last.md", BuildInstructionContent("ZZ last", "Always"));
        sut.AddFile("instructions-aa-first.md", BuildInstructionContent("AA first", "Always"));
        sut.AddFile("instructions-mm-middle.md", BuildInstructionContent("MM middle", "Always"));

        var result = await sut.FetchAsync(
            "https://gitlab.example.com",
            "acme",
            "repo",
            "main",
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Equal("instructions-aa-first.md", result[0].FileName);
        Assert.Equal("instructions-mm-middle.md", result[1].FileName);
        Assert.Equal("instructions-zz-last.md", result[2].FileName);
    }

    private sealed class TestableGitLabRepositoryInstructionFetcher : GitLabRepositoryInstructionFetcher
    {
        private readonly List<(string fileName, string content)> _files = [];
        private bool _folderExists = true;

        public TestableGitLabRepositoryInstructionFetcher()
            : base(
                Substitute.For<IClientScmConnectionRepository>(),
                Substitute.For<IHttpClientFactory>(),
                Substitute.For<ILogger<GitLabRepositoryInstructionFetcher>>())
        {
        }

        public void AddFile(string fileName, string content)
        {
            this._files.Add((fileName, content));
        }

        public void SetFolderAbsent()
        {
            this._folderExists = false;
        }

        protected override Task<IReadOnlyList<(string FileName, string Content)>?> FetchInstructionFilesAsync(
            string organizationUrl,
            string repositoryId,
            string targetBranch,
            Guid? clientId,
            CancellationToken cancellationToken)
        {
            if (!this._folderExists)
            {
                return Task.FromResult<IReadOnlyList<(string FileName, string Content)>?>(null);
            }

            return Task.FromResult<IReadOnlyList<(string FileName, string Content)>?>(
                this._files.Select(file => (file.fileName, file.content)).ToList().AsReadOnly());
        }
    }
}
