// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type {
  AdoBranchOptionDto,
  AdoSourceOptionDto,
  ClientAdoOrganizationScopeDto,
} from '@/services/adoDiscoveryService'
import type {
  ProCursorKnowledgeSourceDto,
  ProCursorRefreshTriggerMode,
  ProCursorSourceKind,
  ProCursorTrackedBranchDto,
} from '@/services/proCursorService'

// Pure sorting/formatting helpers for the ProCursor client tab. Extracted from
// ClientProCursorTab.vue so the component holds only state + orchestration.

export function sortSources(items: ProCursorKnowledgeSourceDto[]): ProCursorKnowledgeSourceDto[] {
  return [...items].sort((left, right) => {
    return (left.displayName ?? '').localeCompare(right.displayName ?? '', undefined, {
      sensitivity: 'base',
    })
  })
}

export function sortBranches(items: ProCursorTrackedBranchDto[]): ProCursorTrackedBranchDto[] {
  return [...items].sort((left, right) => {
    return (left.branchName ?? '').localeCompare(right.branchName ?? '', undefined, {
      sensitivity: 'base',
    })
  })
}

export function sortOrganizationScopes(items: ClientAdoOrganizationScopeDto[]): ClientAdoOrganizationScopeDto[] {
  return [...items].sort((left, right) => {
    const leftLabel = (left.displayName || left.organizationUrl || '').trim()
    const rightLabel = (right.displayName || right.organizationUrl || '').trim()
    return leftLabel.localeCompare(rightLabel, undefined, { sensitivity: 'base' })
  })
}

export function sortProjects(items: Array<{ projectId?: string | null; projectName?: string | null }>) {
  return [...items].sort((left, right) => {
    return (left.projectName || left.projectId || '').localeCompare(right.projectName || right.projectId || '', undefined, {
      sensitivity: 'base',
    })
  })
}

export function sortSourceOptions(items: AdoSourceOptionDto[]): AdoSourceOptionDto[] {
  return [...items].sort((left, right) => {
    return (left.displayName || left.canonicalSourceRef?.value || '').localeCompare(
      right.displayName || right.canonicalSourceRef?.value || '',
      undefined,
      { sensitivity: 'base' },
    )
  })
}

export function sortDiscoveredBranches(items: AdoBranchOptionDto[]): AdoBranchOptionDto[] {
  return [...items].sort((left, right) => {
    if (Boolean(left.isDefault) !== Boolean(right.isDefault)) {
      return left.isDefault ? -1 : 1
    }

    return (left.branchName || '').localeCompare(right.branchName || '', undefined, {
      sensitivity: 'base',
    })
  })
}

export function toErrorMessage(cause: unknown, fallback: string): string {
  return cause instanceof Error && cause.message ? cause.message : fallback
}

export function trimOptional(value: string): string | null {
  const trimmed = value.trim()
  return trimmed ? trimmed : null
}

export function sourceOptionKey(sourceOption: AdoSourceOptionDto): string {
  const provider = sourceOption.canonicalSourceRef?.provider?.trim()
  const value = sourceOption.canonicalSourceRef?.value?.trim()
  return provider && value ? `${provider}::${value}` : ''
}

export function formatOrganizationScopeLabel(scope: ClientAdoOrganizationScopeDto): string {
  const displayName = scope.displayName?.trim()
  const organizationUrl = scope.organizationUrl?.trim() || 'Unnamed organization'
  return displayName && !displayName.localeCompare(organizationUrl, undefined, { sensitivity: 'base' })
    ? displayName
    : displayName
      ? `${displayName} (${organizationUrl})`
      : organizationUrl
}

export function formatBranchOptionLabel(branch: AdoBranchOptionDto): string {
  return branch.isDefault ? `${branch.branchName || 'Unnamed branch'} (default)` : branch.branchName || 'Unnamed branch'
}

export function refreshKeyForSource(sourceId?: string): string {
  return `source:${sourceId ?? 'missing'}`
}

export function refreshKeyForBranch(branchId?: string): string {
  return `branch:${branchId ?? 'missing'}`
}

export function formatNumber(value?: number | null): string {
  return new Intl.NumberFormat('en-US').format(value ?? 0)
}

export function formatUsd(value?: number | null): string {
  if (value == null) {
    return 'Cost n/a'
  }

  return new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(value)
}

export function formatBucketDate(value?: string | null): string {
  if (!value) {
    return 'Unknown bucket'
  }

  const parsed = new Date(`${value}T00:00:00`)
  if (Number.isNaN(parsed.getTime())) {
    return value
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
  }).format(parsed)
}

export function formatSourceKind(kind?: ProCursorSourceKind): string {
  return kind === 'adoWiki' ? 'ADO Wiki' : 'Repository'
}

export function formatTriggerMode(mode?: ProCursorRefreshTriggerMode): string {
  return mode === 'branchUpdate' ? 'On branch update' : 'Manual only'
}

export function formatSymbolMode(mode?: string | null): string {
  if (!mode || mode === 'auto') {
    return 'Auto'
  }

  if (mode === 'text_only') {
    return 'Text only'
  }

  return mode
}

export function formatStatus(value?: string | null): string {
  if (!value) {
    return 'Unknown'
  }

  return value
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .replace(/[_-]/g, ' ')
    .replace(/\b\w/g, (char) => char.toUpperCase())
}

export function statusChipClass(value?: string | null): string {
  const normalized = (value ?? '').toLowerCase()

  if (!normalized) {
    return 'chip-muted'
  }

  if (normalized.includes('fresh') || normalized.includes('enabled') || normalized.includes('ready') || normalized.includes('complete')) {
    return 'chip-success'
  }

  if (normalized.includes('stale') || normalized.includes('pending') || normalized.includes('processing') || normalized.includes('queue') || normalized.includes('lag')) {
    return 'chip-warning'
  }

  if (normalized.includes('fail') || normalized.includes('error') || normalized.includes('disabled') || normalized.includes('cancel')) {
    return 'chip-danger'
  }

  return 'chip-muted'
}

export function formatSha(value?: string | null): string {
  return value ? value.slice(0, 10) : 'n/a'
}

export function formatDate(value?: string | null): string {
  if (!value) {
    return 'Never'
  }

  const parsed = new Date(value)
  if (Number.isNaN(parsed.getTime())) {
    return value
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(parsed)
}
