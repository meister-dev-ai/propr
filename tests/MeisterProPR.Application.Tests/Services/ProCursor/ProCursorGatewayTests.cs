// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Services.ProCursor;

/// <summary>
///     Unit tests for <see cref="ProCursorGateway" />.
/// </summary>
public sealed class ProCursorGatewayTests
{
    [Fact]
    public async Task ListSourcesAsync_WithMultipleSources_AvoidsConcurrentSnapshotRepositoryAccess()
    {
        var clientId = Guid.NewGuid();
        var clientAdminService = Substitute.For<IClientAdminService>();
        var organizationScopeRepository = Substitute.For<IClientAdoOrganizationScopeRepository>();
        var adoDiscoveryService = Substitute.For<IAdoDiscoveryService>();
        var knowledgeSourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();

        var firstSource = CreateSource(clientId, "Repo A", "repo-a");
        var secondSource = CreateSource(clientId, "Repo B", "repo-b");
        var firstBranch = firstSource.AddTrackedBranch(Guid.NewGuid(), "main", ProCursorRefreshTriggerMode.BranchUpdate, true);
        var secondBranch = secondSource.AddTrackedBranch(Guid.NewGuid(), "main", ProCursorRefreshTriggerMode.BranchUpdate, true);
        var firstSnapshot = CreateReadySnapshot(firstSource.Id, firstBranch.Id, "commit-a");
        var secondSnapshot = CreateReadySnapshot(secondSource.Id, secondBranch.Id, "commit-b");

        clientAdminService.ExistsAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        knowledgeSourceRepository.ListByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([firstSource, secondSource]));

        var snapshotRepository = new ConcurrencyDetectingSnapshotRepository(new Dictionary<Guid, IReadOnlyList<ProCursorIndexSnapshot>>
        {
            [firstSource.Id] = [firstSnapshot],
            [secondSource.Id] = [secondSnapshot],
        });

        var gateway = new ProCursorGateway(
            clientAdminService,
            organizationScopeRepository,
            adoDiscoveryService,
            knowledgeSourceRepository,
            snapshotRepository,
            null!,
            null!,
            NullLogger<ProCursorGateway>.Instance);

        var result = await gateway.ListSourcesAsync(clientId, CancellationToken.None);

        Assert.Collection(
            result,
            first =>
            {
                Assert.Equal(firstSource.Id, first.Id);
                Assert.NotNull(first.LatestSnapshot);
                Assert.Equal(firstSnapshot.Id, first.LatestSnapshot!.Id);
                Assert.Single(first.TrackedBranches);
                Assert.Equal(firstBranch.Id, first.TrackedBranches[0].Id);
            },
            second =>
            {
                Assert.Equal(secondSource.Id, second.Id);
                Assert.NotNull(second.LatestSnapshot);
                Assert.Equal(secondSnapshot.Id, second.LatestSnapshot!.Id);
                Assert.Single(second.TrackedBranches);
                Assert.Equal(secondBranch.Id, second.TrackedBranches[0].Id);
            });
    }

    [Fact]
    public async Task CreateSourceAsync_WhenGuidedSourceSelectionIsStale_ThrowsInvalidOperationException()
    {
        var clientId = Guid.NewGuid();
        var scopeId = Guid.NewGuid();
        var request = CreateGuidedRequest(scopeId);
        var clientAdminService = Substitute.For<IClientAdminService>();
        var organizationScopeRepository = Substitute.For<IClientAdoOrganizationScopeRepository>();
        var adoDiscoveryService = Substitute.For<IAdoDiscoveryService>();
        var knowledgeSourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();

        clientAdminService.ExistsAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        organizationScopeRepository.GetByIdAsync(clientId, scopeId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientAdoOrganizationScopeDto?>(CreateOrganizationScope(clientId, scopeId)));
        adoDiscoveryService.ListSourcesAsync(clientId, scopeId, "project-a", ProCursorSourceKind.Repository, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AdoSourceOptionDto>>([]));
        knowledgeSourceRepository.ExistsAsync(Arg.Any<Guid>(), Arg.Any<ProCursorSourceKind>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var gateway = CreateGateway(
            clientAdminService,
            organizationScopeRepository,
            adoDiscoveryService,
            knowledgeSourceRepository);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateway.CreateSourceAsync(clientId, request, CancellationToken.None));

        Assert.Contains("no longer available", ex.Message, StringComparison.OrdinalIgnoreCase);
        await knowledgeSourceRepository.DidNotReceive().AddAsync(Arg.Any<ProCursorKnowledgeSource>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateSourceAsync_WhenGuidedDefaultBranchIsStale_ThrowsInvalidOperationException()
    {
        var clientId = Guid.NewGuid();
        var scopeId = Guid.NewGuid();
        var request = CreateGuidedRequest(scopeId);
        var clientAdminService = Substitute.For<IClientAdminService>();
        var organizationScopeRepository = Substitute.For<IClientAdoOrganizationScopeRepository>();
        var adoDiscoveryService = Substitute.For<IAdoDiscoveryService>();
        var knowledgeSourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();

        clientAdminService.ExistsAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        organizationScopeRepository.GetByIdAsync(clientId, scopeId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientAdoOrganizationScopeDto?>(CreateOrganizationScope(clientId, scopeId)));
        adoDiscoveryService.ListSourcesAsync(clientId, scopeId, "project-a", ProCursorSourceKind.Repository, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AdoSourceOptionDto>>([
                new AdoSourceOptionDto(
                    ProCursorSourceKind.Repository.ToString("G"),
                    new CanonicalSourceReferenceDto("azureDevOps", "repo-guided"),
                    "Guided Repo",
                    "main"),
            ]));
        adoDiscoveryService.ListBranchesAsync(
                clientId,
                scopeId,
                "project-a",
                ProCursorSourceKind.Repository,
                Arg.Any<CanonicalSourceReferenceDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AdoBranchOptionDto>>([
                new AdoBranchOptionDto("develop", true),
            ]));
        knowledgeSourceRepository.ExistsAsync(Arg.Any<Guid>(), Arg.Any<ProCursorSourceKind>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var gateway = CreateGateway(
            clientAdminService,
            organizationScopeRepository,
            adoDiscoveryService,
            knowledgeSourceRepository);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateway.CreateSourceAsync(clientId, request, CancellationToken.None));

        Assert.Contains("default branch", ex.Message, StringComparison.OrdinalIgnoreCase);
        await knowledgeSourceRepository.DidNotReceive().AddAsync(Arg.Any<ProCursorKnowledgeSource>(), Arg.Any<CancellationToken>());
    }

    private static ProCursorKnowledgeSource CreateSource(Guid clientId, string displayName, string repositoryId)
    {
        return new ProCursorKnowledgeSource(
            Guid.NewGuid(),
            clientId,
            displayName,
            ProCursorSourceKind.Repository,
            "https://dev.azure.com/test-org",
            "project-a",
            repositoryId,
            "main",
            null,
            true,
            "auto");
    }

    private static ClientAdoOrganizationScopeDto CreateOrganizationScope(Guid clientId, Guid scopeId)
    {
        return new ClientAdoOrganizationScopeDto(
            scopeId,
            clientId,
            "https://dev.azure.com/test-org",
            "Test Org",
            true,
            AdoOrganizationVerificationStatus.Verified,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private static ProCursorKnowledgeSourceRegistrationRequest CreateGuidedRequest(Guid scopeId)
    {
        return new ProCursorKnowledgeSourceRegistrationRequest(
            "Guided Repo",
            ProCursorSourceKind.Repository,
            null,
            "project-a",
            null,
            "main",
            null,
            "auto",
            [new ProCursorTrackedBranchCreateRequest("main", ProCursorRefreshTriggerMode.BranchUpdate, true)],
            scopeId,
            new CanonicalSourceReferenceDto("azureDevOps", "repo-guided"),
            "Guided Repo");
    }

    private static ProCursorGateway CreateGateway(
        IClientAdminService clientAdminService,
        IClientAdoOrganizationScopeRepository organizationScopeRepository,
        IAdoDiscoveryService adoDiscoveryService,
        IProCursorKnowledgeSourceRepository knowledgeSourceRepository)
    {
        return new ProCursorGateway(
            clientAdminService,
            organizationScopeRepository,
            adoDiscoveryService,
            knowledgeSourceRepository,
            Substitute.For<IProCursorIndexSnapshotRepository>(),
            null!,
            null!,
            NullLogger<ProCursorGateway>.Instance);
    }

    private static ProCursorIndexSnapshot CreateReadySnapshot(Guid sourceId, Guid trackedBranchId, string commitSha)
    {
        var snapshot = new ProCursorIndexSnapshot(
            Guid.NewGuid(),
            sourceId,
            trackedBranchId,
            commitSha,
            "full");

        snapshot.MarkReady(5, 8, 2, true);
        return snapshot;
    }

    private sealed class ConcurrencyDetectingSnapshotRepository(
        IReadOnlyDictionary<Guid, IReadOnlyList<ProCursorIndexSnapshot>> snapshotsBySource)
        : IProCursorIndexSnapshotRepository
    {
        private int _activeOperations;

        public Task AddAsync(ProCursorIndexSnapshot snapshot, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ProCursorIndexSnapshot?> GetByIdAsync(Guid snapshotId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ProCursorIndexSnapshot?> GetLatestAsync(
            Guid knowledgeSourceId,
            Guid? trackedBranchId = null,
            CancellationToken ct = default)
        {
            return this.RunWithoutOverlapAsync(() =>
            {
                var snapshots = this.GetSnapshots(knowledgeSourceId);
                var latestSnapshot = snapshots
                    .Where(snapshot => !trackedBranchId.HasValue || snapshot.TrackedBranchId == trackedBranchId.Value)
                    .OrderByDescending(snapshot => snapshot.CreatedAt)
                    .ThenByDescending(snapshot => snapshot.CompletedAt)
                    .FirstOrDefault();

                return latestSnapshot;
            });
        }

        public Task<ProCursorIndexSnapshot?> GetLatestReadyAsync(
            Guid knowledgeSourceId,
            Guid? trackedBranchId = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProCursorKnowledgeChunk>> ListKnowledgeChunksAsync(
            Guid snapshotId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProCursorIndexSnapshot>> ListBySourceAsync(Guid knowledgeSourceId, CancellationToken ct = default)
        {
            return this.RunWithoutOverlapAsync(() => this.GetSnapshots(knowledgeSourceId));
        }

        public Task ReplaceKnowledgeChunksAsync(
            Guid snapshotId,
            IReadOnlyList<ProCursorKnowledgeChunk> chunks,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ReplaceSymbolGraphAsync(
            Guid snapshotId,
            IReadOnlyList<ProCursorSymbolRecord> symbols,
            IReadOnlyList<ProCursorSymbolEdge> edges,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateAsync(ProCursorIndexSnapshot snapshot, CancellationToken ct = default) =>
            throw new NotSupportedException();

        private IReadOnlyList<ProCursorIndexSnapshot> GetSnapshots(Guid knowledgeSourceId)
        {
            return snapshotsBySource.TryGetValue(knowledgeSourceId, out var snapshots)
                ? snapshots
                : [];
        }

        private async Task<T> RunWithoutOverlapAsync<T>(Func<T> operation)
        {
            if (Interlocked.Increment(ref this._activeOperations) != 1)
            {
                Interlocked.Decrement(ref this._activeOperations);
                throw new InvalidOperationException("Concurrent snapshot access detected.");
            }

            try
            {
                await Task.Yield();
                return operation();
            }
            finally
            {
                Interlocked.Decrement(ref this._activeOperations);
            }
        }
    }
}
