import type { ClientScmConnectionDto } from '@/services/providerConnectionsService'
import type { DiscoveryDescriptor, DiscoverySource, DiscoverySelection } from '@/services/providerDiscoveryService'

export function connectionFixture(id = 'connection-1', providerFamily: ClientScmConnectionDto['providerFamily'] = 'azureDevOps'): ClientScmConnectionDto {
  return {
    id, clientId: 'client-1', providerFamily, hostBaseUrl: 'https://scm.example.com',
    authenticationKind: 'personalAccessToken', displayName: id, isActive: true,
    verificationStatus: 'verified', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  }
}

export const descriptorFixture: DiscoveryDescriptor = {
  provider: 'azureDevOps', scopeLabel: 'Native boundary', projectLabel: 'Native workspace',
  sourceKinds: [{ kind: 'repository', label: 'Native repository' }, { kind: 'adoWiki', label: 'Native documentation' }],
  supportsBranches: true, supportsKnowledgeSources: true,
}

export function sourceFixture(projectId = 'project-1', repositoryId = 'repo-1'): DiscoverySource {
  return {
    organizationScopeId: 'scope-1', providerScopePath: 'https://scm.example.com/native',
    providerProjectKey: projectId, repositoryId, sourceKind: 'repository',
    canonicalSourceRef: { provider: 'azureDevOps', value: repositoryId }, displayName: 'Repository One', defaultBranch: 'main',
  }
}

export function selectionFixture(projectId = 'project-1', connectionId = 'connection-1'): DiscoverySelection {
  return {
    provider: 'azureDevOps', connectionId, scopeKey: 'native-scope', organizationScopeId: 'scope-1',
    providerScopePath: 'https://scm.example.com/native', providerProjectKey: projectId,
  }
}
