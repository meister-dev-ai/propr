// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { components } from '@/types'

export type FindingRiskAssessment = components['schemas']['FindingRiskAssessment']
export type RiskDimension = 'impact' | 'likelihood' | 'riskSeverity' | 'confidence'
export interface FindingRiskFilters { impact?: string; likelihood?: string; riskSeverity?: string; confidence?: string }

export const RISK_DIMENSIONS: readonly { key: RiskDimension; label: string }[] = [
  { key: 'riskSeverity', label: 'Risk severity' },
  { key: 'impact', label: 'Impact' },
  { key: 'likelihood', label: 'Likelihood' },
  { key: 'confidence', label: 'Confidence' },
]
export const RISK_OPTIONS = {
  impact: ['unknown', 'low', 'moderate', 'high', 'critical'],
  likelihood: ['unknown', 'unlikely', 'possible', 'likely', 'notApplicable'],
  riskSeverity: ['unknown', 'low', 'medium', 'high', 'critical'],
  confidence: ['unknown', 'low', 'medium', 'high', 'certain'],
} as const

export function riskLabel(value: string | null | undefined): string {
  if (!value || value.toLowerCase() === 'unknown') return 'Unassessed'
  if (value.toLowerCase() === 'notapplicable') return 'Not applicable'
  return value.charAt(0).toUpperCase() + value.slice(1).toLowerCase()
}

function enumValue<T extends string>(value: unknown, options: readonly T[]): T | 'unknown' {
  return options.find(option => option === value) ?? 'unknown'
}
function text(value: unknown, maximum = 1024): string | null {
  return typeof value === 'string' && value.trim() && value.length <= maximum ? value.trim() : null
}

/** Normalizes optional evidence without deriving a new score from client-side data. */
export function findingRisk(assessment: FindingRiskAssessment | null | undefined, authoritative = true) {
  const impact = enumValue(assessment?.impact, RISK_OPTIONS.impact)
  const likelihood = enumValue(assessment?.likelihood, RISK_OPTIONS.likelihood)
  const impactReason = text(assessment?.impactReason)
  const likelihoodReason = text(assessment?.likelihoodReason)
  const confidenceReason = text(assessment?.confidenceReason)
  // Assessments without separate confidence provenance retain their original producer.
  const confidenceSource = assessment?.confidenceSource ?? assessment?.source
  const trusted = authoritative && assessment?.rubricVersion === 'finding-risk-v1' && text(confidenceSource, 128)
  const confidence = trusted && confidenceReason ? enumValue(assessment?.confidence, RISK_OPTIONS.confidence) : 'unknown'
  const supported = authoritative && assessment?.rubricVersion === 'finding-risk-v1' && text(assessment?.source, 128)
    && impactReason && likelihoodReason && impact !== 'unknown'
    && likelihood !== 'unknown' && likelihood !== 'notApplicable'
  return {
    impact, likelihood, impactReason, likelihoodReason, confidence, confidenceReason,
    riskSeverity: supported ? enumValue(assessment?.severity, RISK_OPTIONS.riskSeverity) : 'unknown',
  }
}

export function matchesFindingRisk(assessment: FindingRiskAssessment | null | undefined, filters: FindingRiskFilters): boolean {
  const risk = findingRisk(assessment)
  return RISK_DIMENSIONS.every(({ key }) => !filters[key] || risk[key].toLowerCase() === filters[key]!.toLowerCase())
}

export function riskSeverityRank(assessment: FindingRiskAssessment | null | undefined): number {
  return ['critical', 'high', 'medium', 'low', 'unknown'].indexOf(findingRisk(assessment).riskSeverity)
}
