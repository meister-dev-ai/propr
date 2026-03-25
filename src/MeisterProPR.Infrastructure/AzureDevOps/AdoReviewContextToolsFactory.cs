using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>
///     Creates <see cref="AdoReviewContextTools" /> instances backed by Azure DevOps.
/// </summary>
public sealed class AdoReviewContextToolsFactory(
    VssConnectionFactory connectionFactory,
    IClientAdoCredentialRepository credentialRepository,
    IOptions<AiReviewOptions> options) : IReviewContextToolsFactory
{
    /// <inheritdoc />
    public IReviewContextTools Create(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        Guid? clientId)
    {
        return new AdoReviewContextTools(
            connectionFactory,
            credentialRepository,
            options,
            organizationUrl,
            projectId,
            repositoryId,
            pullRequestId,
            iterationId,
            clientId);
    }
}
