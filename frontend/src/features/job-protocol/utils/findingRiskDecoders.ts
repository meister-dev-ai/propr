// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { RISK_OPTIONS, type FindingRiskAssessment } from '@/features/reviews/findingRisk'

type FieldNames = Record<keyof FindingRiskAssessment, string>
const camelCase: FieldNames = {
  impact: 'impact', likelihood: 'likelihood', confidence: 'confidence', severity: 'severity',
  impactReason: 'impactReason', likelihoodReason: 'likelihoodReason', confidenceReason: 'confidenceReason',
  source: 'source', confidenceSource: 'confidenceSource', rubricVersion: 'rubricVersion',
}
const pascalCase: FieldNames = {
  impact: 'Impact', likelihood: 'Likelihood', confidence: 'Confidence', severity: 'Severity',
  impactReason: 'ImpactReason', likelihoodReason: 'LikelihoodReason', confidenceReason: 'ConfidenceReason',
  source: 'Source', confidenceSource: 'ConfidenceSource', rubricVersion: 'RubricVersion',
}
const snakeCase: FieldNames = {
  impact: 'impact', likelihood: 'likelihood', confidence: 'confidence', severity: 'severity',
  impactReason: 'impact_reason', likelihoodReason: 'likelihood_reason', confidenceReason: 'confidence_reason',
  source: 'source', confidenceSource: 'confidence_source', rubricVersion: 'rubric_version',
}

const numericEnums = {
  impact: { 0: 'unknown', 1: 'low', 2: 'moderate', 3: 'high', 4: 'critical' },
  likelihood: { 0: 'unknown', 1: 'unlikely', 2: 'possible', 3: 'likely', 4: 'notApplicable' },
  confidence: { 0: 'unknown', 1: 'low', 2: 'medium', 3: 'high', 4: 'certain' },
  riskSeverity: { 0: 'unknown', 1: 'low', 2: 'medium', 3: 'high', 4: 'critical' },
} as const

function object(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

function hasFields(value: Record<string, unknown>, shape: FieldNames, excluding?: FieldNames): boolean {
  const excluded = new Set(excluding ? Object.values(excluding) : [])
  return Object.values(shape).some(key => !excluded.has(key) && Object.hasOwn(value, key))
}

function text(value: unknown, maximum = 1024): string | null {
  return typeof value === 'string' && value.trim() && value.length <= maximum ? value.trim() : null
}

function enumValue<T extends string>(value: unknown, options: readonly T[], numericValues?: Readonly<Record<number, T>>): T | 'unknown' {
  if (numericValues && typeof value === 'number' && Number.isInteger(value)) {
    return numericValues[value] ?? 'unknown'
  }
  return typeof value === 'string'
    ? options.find(option => option.toLowerCase() === value.replaceAll('_', '').toLowerCase()) ?? 'unknown'
    : 'unknown'
}

function decode(value: Record<string, unknown>, names: FieldNames, numeric: boolean): FindingRiskAssessment {
  const confidenceSource = value[names.confidenceSource]
  return {
    impact: enumValue(value[names.impact], RISK_OPTIONS.impact, numeric ? numericEnums.impact : undefined),
    likelihood: enumValue(value[names.likelihood], RISK_OPTIONS.likelihood, numeric ? numericEnums.likelihood : undefined),
    confidence: enumValue(value[names.confidence], RISK_OPTIONS.confidence, numeric ? numericEnums.confidence : undefined),
    severity: enumValue(value[names.severity], RISK_OPTIONS.riskSeverity, numeric ? numericEnums.riskSeverity : undefined),
    impactReason: text(value[names.impactReason]), likelihoodReason: text(value[names.likelihoodReason]),
    confidenceReason: text(value[names.confidenceReason]), source: text(value[names.source], 128),
    rubricVersion: text(value[names.rubricVersion], 128),
    // A missing confidence provenance means that the assessment's producer set the confidence; an invalid explicit
    // provenance becomes empty, so it does not fall back to that producer.
    confidenceSource: confidenceSource == null ? null : text(confidenceSource, 128) ?? '',
  }
}

/** Reads retained server diagnostics whose nested assessments use camelCase or PascalCase and numeric enums. */
export function decodeProtocolRiskAssessment(input: unknown): FindingRiskAssessment | null {
  const value = object(input)
  if (!value || hasFields(value, snakeCase, camelCase)) return null
  if (hasFields(value, pascalCase)) {
    return hasFields(value, camelCase) ? null : decode(value, pascalCase, true)
  }
  return hasFields(value, camelCase) ? decode(value, camelCase, true) : null
}

/** Reads producer output for display without accepting producer-supplied authority. */
export function decodeProposedRiskAssessment(input: unknown): FindingRiskAssessment | null {
  const value = object(input)
  if (!value || hasFields(value, pascalCase)) return null
  const snake = hasFields(value, snakeCase, camelCase)
  if (snake && hasFields(value, camelCase, snakeCase)) return null
  if (!hasFields(value, snake ? snakeCase : camelCase)) return null
  const proposed = decode(value, snake ? snakeCase : camelCase, false)
  return { ...proposed, source: null, confidenceSource: '', rubricVersion: null, confidence: 'unknown', severity: 'unknown' }
}
