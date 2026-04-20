export type ProviderFamily = 'azureDevOps' | 'github' | 'gitLab' | 'forgejo'
export type ProviderAuthenticationKind = 'oauthClientCredentials' | 'personalAccessToken' | 'appInstallation'
export type ProviderScopeType = 'organization' | 'project' | 'group' | 'repository'
export type ProviderVerificationStatus = 'verified' | 'stale' | 'failed'
export type ProviderReadinessLevel = 'configured' | 'degraded' | 'onboardingReady' | 'workflowComplete'

export interface ProviderConnectionFixture {
  id: string
  clientId: string
  providerFamily: ProviderFamily
  hostBaseUrl: string
  authenticationKind: ProviderAuthenticationKind
  displayName: string
  isActive: boolean
  verificationStatus: ProviderVerificationStatus
  readinessLevel: ProviderReadinessLevel
  readinessReason: string
  hostVariant: 'hosted' | 'selfHosted'
  missingReadinessCriteria: string[]
  lastVerifiedAt: string | null
  lastVerificationError: string | null
  createdAt: string
  updatedAt: string
}

export interface ProviderScopeFixture {
  id: string
  clientId: string
  connectionId: string
  scopeType: ProviderScopeType
  externalScopeId: string
  scopePath: string
  displayName: string
  verificationStatus: ProviderVerificationStatus
  isEnabled: boolean
  lastVerifiedAt: string | null
  lastVerificationError: string | null
  createdAt: string
  updatedAt: string
}

export interface ProviderReviewerIdentityFixture {
  id: string
  clientId: string
  connectionId: string
  providerFamily: ProviderFamily
  externalUserId: string
  login: string
  displayName: string
  isBot: boolean
  updatedAt: string
}

export interface ProviderReviewDraftFixture {
  clientId: string
  providerFamily: ProviderFamily
  hostBaseUrl: string
  repositoryReference: {
    externalRepositoryId: string
    ownerOrNamespace: string
    projectPath: string
    displayName: string
  }
  codeReviewReference: {
    externalReviewId: string
    number: number
    webUrl: string
    sourceBranch: string
    targetBranch: string
  }
  reviewRevision: {
    headSha: string
    baseSha: string
    startSha: string
    providerRevisionId: string
    patchIdentity: string
  }
  requestedReviewerIdentity: {
    externalUserId: string
    login: string
    displayName: string
    isBot: boolean
  }
}

const anchorTime = Date.parse('2026-04-08T12:00:00Z')

function isoHoursAgo(hours: number) {
  return new Date(anchorTime - hours * 60 * 60 * 1000).toISOString()
}

export const providerConnectionsByClientFixture: Record<string, ProviderConnectionFixture[]> = {
  '1': [
    {
      id: 'provider-conn-ado-1',
      clientId: '1',
      providerFamily: 'azureDevOps',
      hostBaseUrl: 'https://dev.azure.com',
      authenticationKind: 'oauthClientCredentials',
      displayName: 'Meister Azure DevOps',
      isActive: true,
      verificationStatus: 'verified',
      readinessLevel: 'workflowComplete',
      readinessReason: 'Connection meets onboarding and workflow-complete readiness criteria.',
      hostVariant: 'hosted',
      missingReadinessCriteria: [],
      lastVerifiedAt: isoHoursAgo(4),
      lastVerificationError: null,
      createdAt: isoHoursAgo(168),
      updatedAt: isoHoursAgo(4),
    },
    {
      id: 'provider-conn-github-1',
      clientId: '1',
      providerFamily: 'github',
      hostBaseUrl: 'https://github.com',
      authenticationKind: 'personalAccessToken',
      displayName: 'Acme GitHub',
      isActive: true,
      verificationStatus: 'verified',
      readinessLevel: 'workflowComplete',
      readinessReason: 'Connection meets onboarding and workflow-complete readiness criteria.',
      hostVariant: 'hosted',
      missingReadinessCriteria: [],
      lastVerifiedAt: isoHoursAgo(2),
      lastVerificationError: null,
      createdAt: isoHoursAgo(72),
      updatedAt: isoHoursAgo(2),
    },
  ],
  '2': [
    {
      id: 'provider-conn-gitlab-1',
      clientId: '2',
      providerFamily: 'gitLab',
      hostBaseUrl: 'https://gitlab.example.com',
      authenticationKind: 'personalAccessToken',
      displayName: 'Platform GitLab',
      isActive: false,
      verificationStatus: 'stale',
      readinessLevel: 'degraded',
      readinessReason: 'Connection is disabled.',
      hostVariant: 'selfHosted',
      missingReadinessCriteria: ['Connection must be active.'],
      lastVerifiedAt: isoHoursAgo(36),
      lastVerificationError: 'Token missing read_api scope.',
      createdAt: isoHoursAgo(240),
      updatedAt: isoHoursAgo(36),
    },
  ],
}

export const providerScopesByConnectionFixture: Record<string, ProviderScopeFixture[]> = {
  'provider-conn-ado-1': [
    {
      id: 'provider-scope-ado-1',
      clientId: '1',
      connectionId: 'provider-conn-ado-1',
      scopeType: 'organization',
      externalScopeId: 'meister-propr',
      scopePath: 'meister-propr',
      displayName: 'Meister Org',
      verificationStatus: 'verified',
      isEnabled: true,
      lastVerifiedAt: isoHoursAgo(4),
      lastVerificationError: null,
      createdAt: isoHoursAgo(168),
      updatedAt: isoHoursAgo(4),
    },
  ],
  'provider-conn-github-1': [
    {
      id: 'provider-scope-github-1',
      clientId: '1',
      connectionId: 'provider-conn-github-1',
      scopeType: 'organization',
      externalScopeId: 'acme',
      scopePath: 'acme',
      displayName: 'Acme',
      verificationStatus: 'verified',
      isEnabled: true,
      lastVerifiedAt: isoHoursAgo(2),
      lastVerificationError: null,
      createdAt: isoHoursAgo(72),
      updatedAt: isoHoursAgo(2),
    },
  ],
  'provider-conn-gitlab-1': [
    {
      id: 'provider-scope-gitlab-1',
      clientId: '2',
      connectionId: 'provider-conn-gitlab-1',
      scopeType: 'group',
      externalScopeId: 'acme/platform',
      scopePath: 'acme/platform',
      displayName: 'acme/platform',
      verificationStatus: 'stale',
      isEnabled: false,
      lastVerifiedAt: isoHoursAgo(36),
      lastVerificationError: 'The scope is no longer reachable with the stored token.',
      createdAt: isoHoursAgo(240),
      updatedAt: isoHoursAgo(36),
    },
  ],
}

export const providerReviewerIdentitiesByConnectionFixture: Record<string, ProviderReviewerIdentityFixture | null> = {
  'provider-conn-ado-1': {
    id: 'provider-reviewer-ado-1',
    clientId: '1',
    connectionId: 'provider-conn-ado-1',
    providerFamily: 'azureDevOps',
    externalUserId: 'ado-reviewer-1',
    login: 'meister-bot',
    displayName: 'Meister Bot',
    isBot: true,
    updatedAt: isoHoursAgo(4),
  },
  'provider-conn-github-1': {
    id: 'provider-reviewer-github-1',
    clientId: '1',
    connectionId: 'provider-conn-github-1',
    providerFamily: 'github',
    externalUserId: 'github-reviewer-1',
    login: 'meister-dev-bot',
    displayName: 'Meister Dev Bot',
    isBot: true,
    updatedAt: isoHoursAgo(2),
  },
  'provider-conn-gitlab-1': null,
}

export const providerReviewerIdentityCandidatesByConnectionFixture: Record<string, ProviderReviewerIdentityFixture[]> = {
  'provider-conn-ado-1': [
    {
      id: 'provider-reviewer-ado-1',
      clientId: '1',
      connectionId: 'provider-conn-ado-1',
      providerFamily: 'azureDevOps',
      externalUserId: 'ado-reviewer-1',
      login: 'meister-bot',
      displayName: 'Meister Bot',
      isBot: true,
      updatedAt: isoHoursAgo(4),
    },
  ],
  'provider-conn-github-1': [
    {
      id: 'provider-reviewer-github-1',
      clientId: '1',
      connectionId: 'provider-conn-github-1',
      providerFamily: 'github',
      externalUserId: 'github-reviewer-1',
      login: 'meister-dev-bot',
      displayName: 'Meister Dev Bot',
      isBot: true,
      updatedAt: isoHoursAgo(2),
    },
    {
      id: 'provider-reviewer-github-2',
      clientId: '1',
      connectionId: 'provider-conn-github-1',
      providerFamily: 'github',
      externalUserId: 'github-reviewer-2',
      login: 'meister-maintainer',
      displayName: 'Meister Maintainer',
      isBot: false,
      updatedAt: isoHoursAgo(6),
    },
  ],
  'provider-conn-gitlab-1': [
    {
      id: 'provider-reviewer-gitlab-1',
      clientId: '2',
      connectionId: 'provider-conn-gitlab-1',
      providerFamily: 'gitLab',
      externalUserId: 'gitlab-reviewer-1',
      login: 'meister-reviewer',
      displayName: 'Meister Reviewer',
      isBot: true,
      updatedAt: isoHoursAgo(36),
    },
  ],
}

export const providerReviewDraftFixture: ProviderReviewDraftFixture = {
  clientId: '1',
  providerFamily: 'github',
  hostBaseUrl: 'https://github.com',
  repositoryReference: {
    externalRepositoryId: 'repo-gh-1',
    ownerOrNamespace: 'acme',
    projectPath: 'acme/propr',
    displayName: 'propr',
  },
  codeReviewReference: {
    externalReviewId: '42',
    number: 42,
    webUrl: 'https://github.com/acme/propr/pull/42',
    sourceBranch: 'refs/heads/feature/provider-neutral',
    targetBranch: 'refs/heads/main',
  },
  reviewRevision: {
    headSha: '1111111111111111111111111111111111111111',
    baseSha: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
    startSha: '0000000000000000000000000000000000000000',
    providerRevisionId: 'revision-1',
    patchIdentity: 'aaaaaaaa..11111111',
  },
  requestedReviewerIdentity: {
    externalUserId: 'github-reviewer-1',
    login: 'meister-dev-bot',
    displayName: 'Meister Dev Bot',
    isBot: true,
  },
}

export function cloneProviderFixtures<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}
