// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import type {
  AdoBranchOptionDto,
  AdoCrawlFilterOptionDto,
  AdoProjectOptionDto,
  CanonicalSourceReferenceDto,
  ClientAdoOrganizationScopeDto,
} from '@/services/adoDiscoveryService'
import type { ProCursorKnowledgeSourceDto } from '@/services/proCursorService'
import type { ScmProvider } from './crawlConfigForm.types'

// Pure normalization/format/sort helpers for the crawl-config form. Extracted
// from CrawlConfigForm.vue so the component holds only state + orchestration.

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export function normalizeText(value: string | null | undefined): string {
  return value?.trim() ?? ''
}

export function normalizeProvider(value: string | null | undefined): ScmProvider {
  switch (value) {
    case 'github':
    case 'gitLab':
    case 'forgejo':
      return value
    default:
      return 'azureDevOps'
  }
}

export function formatProvider(value: ScmProvider): string {
  switch (value) {
    case 'gitLab':
      return 'GitLab'
    case 'forgejo':
      return 'Forgejo'
    case 'github':
      return 'GitHub'
    default:
      return 'Azure DevOps'
  }
}

export function normalizeStringList(values: ReadonlyArray<string | null | undefined> | null | undefined): string[] {
  const normalizedValues: string[] = []
  const seen = new Set<string>()

  for (const value of values ?? []) {
    const normalizedValue = normalizeText(value)
    if (!normalizedValue || seen.has(normalizedValue)) {
      continue
    }

    seen.add(normalizedValue)
    normalizedValues.push(normalizedValue)
  }

  return normalizedValues
}

export function isValidUuid(value: string): boolean {
  return uuidPattern.test(value)
}

export function cloneCanonicalSourceRef(canonicalSourceRef: CanonicalSourceReferenceDto | null | undefined): CanonicalSourceReferenceDto | null {
  const provider = normalizeText(canonicalSourceRef?.provider)
  const value = normalizeText(canonicalSourceRef?.value)
  if (!provider || !value) {
    return null
  }

  return { provider, value }
}

export function sourceOptionKey(canonicalSourceRef: CanonicalSourceReferenceDto | null | undefined): string {
  const canonical = cloneCanonicalSourceRef(canonicalSourceRef)
  if (!canonical) {
    return ''
  }

  return `${canonical.provider}::${canonical.value}`
}

export function formatOrganizationScopeLabel(scope: ClientAdoOrganizationScopeDto): string {
  const label = normalizeText(scope.displayName) || normalizeText(scope.organizationUrl) || 'Unnamed organization'
  return scope.isEnabled === false ? `${label} (disabled)` : label
}

export function formatProjectLabel(project: AdoProjectOptionDto): string {
  return normalizeText(project.projectName) || normalizeText(project.projectId) || 'Unnamed project'
}

export function sortOrganizationScopes(scopes: ClientAdoOrganizationScopeDto[]): ClientAdoOrganizationScopeDto[] {
  return [...scopes].sort((left, right) => formatOrganizationScopeLabel(left).localeCompare(formatOrganizationScopeLabel(right)))
}

export function sortProjects(discoveredProjects: AdoProjectOptionDto[]): AdoProjectOptionDto[] {
  return [...discoveredProjects].sort((left, right) => formatProjectLabel(left).localeCompare(formatProjectLabel(right)))
}

export function sortCrawlFilterOptions(options: AdoCrawlFilterOptionDto[]): AdoCrawlFilterOptionDto[] {
  return [...options].sort((left, right) => {
    const leftLabel = normalizeText(left.displayName) || sourceOptionKey(left.canonicalSourceRef)
    const rightLabel = normalizeText(right.displayName) || sourceOptionKey(right.canonicalSourceRef)
    return leftLabel.localeCompare(rightLabel)
  })
}

export function formatProCursorSourceLabel(source: ProCursorKnowledgeSourceDto): string {
  return normalizeText(source.displayName) || normalizeText(source.sourceDisplayName) || normalizeText(source.repositoryId) || 'Unnamed source'
}

export function formatProCursorSourcePath(source: ProCursorKnowledgeSourceDto): string {
  const providerScopePath = normalizeText(source.providerScopePath) || 'No organization'
  const sourceDisplayName = normalizeText(source.sourceDisplayName) || normalizeText(source.repositoryId) || 'No selected source'
  return `${providerScopePath} / ${normalizeText(source.providerProjectKey) || 'No project'} / ${sourceDisplayName}`
}

export function sortProCursorSources(sources: ProCursorKnowledgeSourceDto[]): ProCursorKnowledgeSourceDto[] {
  return [...sources].sort((left, right) => formatProCursorSourceLabel(left).localeCompare(formatProCursorSourceLabel(right)))
}

export function sortBranchSuggestions(branchSuggestions: AdoBranchOptionDto[] | null | undefined): AdoBranchOptionDto[] {
  return [...(branchSuggestions ?? [])].sort((left, right) => {
    if (!!left.isDefault !== !!right.isDefault) {
      return left.isDefault ? -1 : 1
    }

    return normalizeText(left.branchName).localeCompare(normalizeText(right.branchName))
  })
}

export function formatBranchSuggestion(branchSuggestion: AdoBranchOptionDto): string {
  const branchName = normalizeText(branchSuggestion.branchName)
  return branchSuggestion.isDefault ? `${branchName} (default)` : branchName
}

export function formatProCursorScopeRepairMessage(repairCount: number): string {
  return repairCount === 1
    ? '1 saved ProCursor source is no longer eligible for this client. That selection was removed locally; save to persist the repaired scope.'
    : `${repairCount} saved ProCursor sources are no longer eligible for this client. Those selections were removed locally; save to persist the repaired scope.`
}
