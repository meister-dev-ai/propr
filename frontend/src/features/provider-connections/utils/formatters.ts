// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { getSupportedAuthenticationKinds } from '@/services/providerActivationService'
import type {
  ProviderConnectionReadinessLevel,
  ScmAuthenticationKind,
  ScmProviderFamily,
} from '@/services/providerConnectionsService'

// Pure label/class/auth-kind helpers for the provider-connections view model.
// Extracted from useProviderConnectionsViewModel.ts so the view model holds
// only reactive state and orchestration.

export function formatProvider(providerFamily: ScmProviderFamily): string {
  switch (providerFamily) {
    case 'gitLab':
      return 'GitLab'
    case 'forgejo':
      return 'Forgejo'
    case 'azureDevOps':
      return 'Azure DevOps'
    default:
      return 'GitHub'
  }
}

export function formatVerification(verificationStatus: string): string {
  if (!verificationStatus) {
    return 'Unknown'
  }

  return verificationStatus.charAt(0).toUpperCase() + verificationStatus.slice(1)
}

export function formatReadiness(readinessLevel: ProviderConnectionReadinessLevel): string {
  switch (readinessLevel) {
    case 'configured':
      return 'Configured'
    case 'degraded':
      return 'Degraded'
    case 'onboardingReady':
      return 'Onboarding Ready'
    case 'workflowComplete':
      return 'Workflow Complete'
    default:
      return 'Unknown'
  }
}

export function verificationChipClass(verificationStatus: string): string {
  switch (verificationStatus) {
    case 'verified':
      return 'chip-success'
    case 'failed':
      return 'chip-danger'
    default:
      return 'chip-muted'
  }
}

export function readinessChipClass(readinessLevel: ProviderConnectionReadinessLevel): string {
  switch (readinessLevel) {
    case 'workflowComplete':
      return 'chip-success'
    case 'degraded':
      return 'chip-danger'
    case 'onboardingReady':
      return 'chip-warning'
    default:
      return 'chip-muted'
  }
}

export function normalizeAuthenticationKind(
  providerFamily: ScmProviderFamily,
  hostBaseUrl: string,
  authenticationKind: ScmAuthenticationKind,
): ScmAuthenticationKind {
  const supportedKinds = getSupportedAuthenticationKinds(providerFamily, hostBaseUrl)
  return supportedKinds.includes(authenticationKind) ? authenticationKind : supportedKinds[0]
}

export function getPreferredAuthenticationKind(providerFamily: ScmProviderFamily, hostBaseUrl: string): ScmAuthenticationKind {
  return getSupportedAuthenticationKinds(providerFamily, hostBaseUrl)[0]
}

export function parseOptionalPositiveNumber(value: string | number): number | null {
  const trimmed = String(value).trim()
  if (!trimmed) {
    return null
  }

  const parsed = Number(trimmed)
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null
}

// A blank value is allowed (the API falls back to the 30-day default); when
// supplied the retention window must be a whole number of days in 1..3650.
export function isRetentionDaysValid(value: string | number): boolean {
  const trimmed = String(value).trim()
  if (!trimmed) {
    return true
  }

  const parsed = Number(trimmed)
  return Number.isInteger(parsed) && parsed >= 1 && parsed <= 3650
}
