// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { http, HttpResponse, delay } from 'msw'
import type {
  PerformanceQuery,
  PerformanceScope,
  PerformanceResponse,
  PerformanceScenario,
  PerformancePoint,
  PerformanceFacets,
  PerformanceMatrixCell,
  PerformanceSavedReport,
  PerformanceExclusion,
} from '@/services/reviewerPerformanceService'
import type { components } from '@/types'
import { riskLabel } from '@/features/reviews/findingRisk'
type Counts = components['schemas']['ReviewerPerformanceCounts']
type Outcomes = components['schemas']['ReviewerPerformanceOutcomeCounts']
type Range = components['schemas']['ReviewerPerformanceRange']
const version = 'reviewer-performance-v2'
const recoveryCatalog = new Map<string, PerformanceExclusion>()
const modelRecovery = new Map<string, number>()
const sourceConnectionId = '30000000-0000-0000-0000-000000000001'
const queuedRecovery = new Map<string, { queuedAt: number; requiresRepair: boolean }>()
const sweepDelay = 5000
const observationDuration = 10000
function recoveryProgress(id: string) {
  const request = queuedRecovery.get(id)
  if (!request) return undefined
  const started = request.queuedAt + sweepDelay
  const completed = started + observationDuration
  return { ...request, started, completed,
    state: Date.now() < started ? 'queued' : Date.now() < completed ? 'in-progress' : request.requiresRepair ? 'repair-required' : 'complete',
  }
}
const clients = [
  { id: '10000000-0000-0000-0000-000000000001', label: 'Payments' },
  { id: '10000000-0000-0000-0000-000000000002', label: 'Platform' },
]
const scopes = [
  { client: clients[0]!.id, repo: 'payments-api', label: 'Payments API' },
  { client: clients[0]!.id, repo: 'payments-ui', label: 'Payments UI' },
  { client: clients[1]!.id, repo: 'platform-service', label: 'Platform Service' },
]
const models = ['review-large', 'review-small', 'review-next'].map((id) => ({
  id: btoa(JSON.stringify([id, 'review'])),
  label: id,
}))
const types = [
  { id: 'logic-error', label: 'Logic error' },
  { id: 'concurrency', label: 'Concurrency' },
]
const qualifiers = ['Missing', 'Incorrect', 'Extraneous'].map((id) => ({ id, label: id }))
const riskValues = [
  { impact: 'Unknown', likelihood: 'Unknown', riskSeverity: 'Unknown', confidence: 'Unknown' },
  { impact: 'Moderate', likelihood: 'Unlikely', riskSeverity: 'Low', confidence: 'Low' },
  { impact: 'Critical', likelihood: 'Likely', riskSeverity: 'Critical', confidence: 'Certain' },
  { impact: 'High', likelihood: 'Possible', riskSeverity: 'High', confidence: 'High' },
  { impact: 'High', likelihood: 'Unlikely', riskSeverity: 'Medium', confidence: 'Medium' },
  { impact: 'Low', likelihood: 'Possible', riskSeverity: 'Low', confidence: 'High' },
  { impact: 'Moderate', likelihood: 'NotApplicable', riskSeverity: 'Unknown', confidence: 'Medium' },
]
const riskFacets = (dimension: 'impact' | 'likelihood' | 'riskSeverity' | 'confidence') =>
  [...new Set(riskValues.map(value => value[dimension]))].map(id => ({ id, label: riskLabel(id) }))
const hasRiskScope = (scope: PerformanceScope, dimension: string, _query: PerformanceQuery) =>
  scope.impacts != null || scope.likelihoods != null || scope.riskSeverities != null || scope.confidences != null ||
  ['impact', 'likelihood', 'riskSeverity', 'confidence'].includes(dimension)
const withoutRisk = (scope: PerformanceScope): PerformanceScope => ({ ...scope, impacts: null, likelihoods: null, riskSeverities: null, confidences: null })
const provider = 'AzureDevOps:https://dev.azure.com/example'
const repoId = (scope: (typeof scopes)[number]) =>
  btoa(JSON.stringify([scope.client, provider, scope.repo]))
const outcomeKeys = [
  'positive',
  'wrong',
  'dismissed',
  'wontFix',
  'byDesign',
  'unknown',
  'unresolved',
] as const
const emptyOutcomes = (): Outcomes => Object.fromEntries(outcomeKeys.map((key) => [key, 0]))
const emptyCounts = (): Counts => ({
  outcomes: emptyOutcomes(),
  confirmedDuplicates: emptyOutcomes(),
  actedMisses: 0,
  unactedMisses: 0,
  provisionalMisses: 0,
  generated: 0,
  suppressedRepeats: 0,
  withheld: 0,
  publicationUnknown: 0,
  duplicateChecked: 0,
  duplicateSuspected: 0,
  duplicateUnknown: 0,
  unclassified: 0,
  unattributed: 0,
  harvestedThreads: 0,
  failedMissJudgements: 0,
})
function add(a: Counts, b: Counts): void {
  for (const key of outcomeKeys) {
    a.outcomes![key] = (a.outcomes?.[key] ?? 0) + (b.outcomes?.[key] ?? 0)
    a.confirmedDuplicates![key] =
      (a.confirmedDuplicates?.[key] ?? 0) + (b.confirmedDuplicates?.[key] ?? 0)
  }
  for (const key of [
    'actedMisses',
    'unactedMisses',
    'provisionalMisses',
    'generated',
    'suppressedRepeats',
    'withheld',
    'publicationUnknown',
    'duplicateChecked',
    'duplicateSuspected',
    'duplicateUnknown',
    'unclassified',
    'unattributed',
    'harvestedThreads',
    'failedMissJudgements',
  ] as const)
    a[key] = (a[key] ?? 0) + (b[key] ?? 0)
}
function summarize(values: (number | null | undefined)[]): Range | undefined {
  const sorted = values.filter((value): value is number => value != null).sort((a, b) => a - b)
  if (!sorted.length) return undefined
  const quantile = (q: number) => {
    const index = (sorted.length - 1) * q
    const low = Math.floor(index)
    const fraction = index - low
    return sorted[low]! + fraction * ((sorted[low + 1] ?? sorted[low])! - sorted[low]!)
  }
  return {
    minimum: sorted[0],
    firstQuartile: quantile(0.25),
    median: quantile(0.5),
    thirdQuartile: quantile(0.75),
    maximum: sorted.at(-1),
    availableScenarios: sorted.length,
  }
}
function score(
  counts: Counts,
  _modelScope: boolean,
): components['schemas']['ReviewerPerformanceScore'] {
  const scenarios: PerformanceScenario[] = []
  for (const dismissed of ['positive', 'negative', 'excluded'])
    for (const wontFix of ['positive', 'negative', 'excluded'])
      for (const byDesign of ['positive', 'negative', 'excluded'])
        for (const missRule of ['acted', 'allSettled'])
          for (const duplicates of ['retain', 'negative', 'excluded']) {
            let tp = 0
            let fp = 0
            let excluded = (counts.outcomes?.unknown ?? 0) + (counts.outcomes?.unresolved ?? 0)
            const contribution = (rule: string, count: number) => {
              if (rule === 'positive') tp += count
              else if (rule === 'negative') fp += count
              else excluded += count
            }
            const rules = { positive: 'positive', wrong: 'negative', dismissed, wontFix, byDesign }
            for (const key of ['positive', 'wrong', 'dismissed', 'wontFix', 'byDesign'] as const) {
              const duplicate = counts.confirmedDuplicates?.[key] ?? 0
              contribution(rules[key], (counts.outcomes?.[key] ?? 0) - duplicate)
              contribution(duplicates === 'retain' ? rules[key] : duplicates, duplicate)
            }
            const fn =
              (counts.actedMisses ?? 0) +
              (missRule === 'allSettled' ? (counts.unactedMisses ?? 0) : 0)
            scenarios.push({
              id: `${version}/dismissed:${dismissed}/wontFix:${wontFix}/byDesign:${byDesign}/misses:${missRule}/duplicates:${duplicates}`,
              dismissed,
              wontFix,
              byDesign,
              misses: missRule,
              duplicates,
              truePositives: tp,
              falsePositives: fp,
              falseNegatives: fn,
              excluded,
              precision: tp + fp ? tp / (tp + fp) : null,
              recall: tp + fn ? tp / (tp + fn) : null,
              f1: 2 * tp + fp + fn ? (2 * tp) / (2 * tp + fp + fn) : null,
            })
          }
  counts.outcomes = {
    ...counts.outcomes,
    total: outcomeKeys.reduce((total, key) => total + (counts.outcomes?.[key] ?? 0), 0),
  }
  counts.confirmedDuplicates = {
    ...counts.confirmedDuplicates,
    total: outcomeKeys.reduce((total, key) => total + (counts.confirmedDuplicates?.[key] ?? 0), 0),
  }
  return {
    counts,
    scenarios,
    summary: {
      precision: summarize(scenarios.map((item) => item.precision)),
      recall: summarize(scenarios.map((item) => item.recall)),
      f1: summarize(scenarios.map((item) => item.f1)),
    },
  }
}
type Cell = {
  impact: string
  likelihood: string
  riskSeverity: string
  confidence: string
  date: string
  client: string
  repository: string
  model: string
  type: string
  qualifier: string
  counts: Counts
  aggregateId: string
  pullRequestId: number
  covered: boolean
  exclusionReason?: string
  modelCovered: boolean
  modelReason?: string
  concerns: { id: string; acted: boolean; detectedBy: string[] }[]
}
function cells(from: string, to: string): Cell[] {
  const result: Cell[] = []
  const today = Math.floor(Date.now() / 86400000)
  const start = Math.floor(Date.parse(from) / 86400000)
  const end = Math.floor(Date.parse(to) / 86400000)
  for (let day = start; day <= end; day++) {
    if (day < today - 59 || day > today) continue
    for (const [s, scope] of scopes.entries())
      for (const [m, model] of models.entries())
        for (const [t, type] of types.entries())
          for (const [k, qualifier] of qualifiers.entries()) {
            if ((m === 0 && day > today - 6) || (m === 2 && day < today - 13) || day % 7 === 0)
              continue
            const counts = emptyCounts()
            const n = day % 5
            counts.outcomes = {
              positive: 7 + (m === 0 ? 5 : 0) + n,
              wrong: 1 + s + (m === 1 ? 2 : 0),
              dismissed: day > today - 8 ? 4 : 2,
              wontFix: day > today - 8 ? 3 : 1,
              byDesign: k === 2 ? 1 : 0,
              unknown: 1,
              unresolved: day > today - 3 ? 2 : 0,
            }
            counts.confirmedDuplicates = {
              positive: 1,
              wrong: 1,
              dismissed: 1,
              wontFix: 0,
              byDesign: 0,
              unknown: 0,
              unresolved: 0,
            }
            counts.actedMisses = 1
            counts.unactedMisses = t === 1 ? 1 : 0
            counts.provisionalMisses = day > today - 3 ? 1 : 0
            counts.harvestedThreads = 2
            counts.generated =
              outcomeKeys.reduce((sum, key) => sum + (counts.outcomes?.[key] ?? 0), 0) + 2
            counts.suppressedRepeats = 1
            counts.withheld = 1
            counts.duplicateChecked = counts.generated! - 4
            counts.duplicateUnknown = 2
            const coverageOnly = (day + s) % 18 === 0
            if (coverageOnly) {
              Object.assign(counts, emptyCounts())
            }
            const exclusionReason = !coverageOnly && (day + s) % 12 === 0
              ? 'miss-judgement-failed'
              : 'miss-collection-coverage-unavailable'
            if ((day + s) % 6 === 0 && !coverageOnly) counts.failedMissJudgements = exclusionReason === 'miss-judgement-failed' ? 1 : 0
            const aggregateId = `20000000-0000-0000-${String(s + 1).padStart(4, '0')}-${String(day).padStart(12, '0')}`
            const recovered = recoveryProgress(aggregateId)?.state === 'complete'
            if (recovered) counts.failedMissJudgements = 0
            const zero = m === 2 && (day + s) % 5 === 0
            if (zero) Object.assign(counts, emptyCounts())
            const noConcerns = (day + s) % 10 === 0
            const modelReason = m === 0 ? 'model-proof-unavailable'
              : (day + s) % 8 === 0 && m === 2 ? 'model-generation-incomplete'
              : !noConcerns && (day + s) % 4 === 0 && m === 2 && !modelRecovery.has(aggregateId) ? 'model-concern-match-unavailable'
              : !recovered && (day + s) % 6 === 0 ? 'model-concern-collection-unavailable' : undefined
            const issue = `${aggregateId}/${type.id}/${qualifier.id}`
            const detectedBy = (day + s) % 5 !== 0 && day >= today - 13 ? [models[2]!.id] : []
            result.push({
              ...riskValues[(day + s + m + t + k) % riskValues.length]!,
              modelCovered: modelReason == null, modelReason,
              concerns: noConcerns ? [] : [{ id: `${issue}/X`, acted: true, detectedBy }, { id: `${issue}/Y`, acted: t === 0, detectedBy: [] }],
              date: new Date(day * 86400000).toISOString().slice(0, 10),
              client: scope.client,
              repository: repoId(scope),
              model: model.id,
              type: type.id,
              qualifier: qualifier.id,
              counts,
              aggregateId,
              pullRequestId: 100 + day,
              covered: recovered || (day + s) % 6 !== 0,
              exclusionReason,
            })
          }
  }
  return result
}
const matches = (cell: Cell, scope: PerformanceScope) =>
  (scope.clientIds == null || scope.clientIds.includes(cell.client)) &&
  (scope.repositories == null || scope.repositories.includes(cell.repository)) &&
  (scope.models == null || scope.models.includes(cell.model)) &&
  (scope.types == null || scope.types.includes(cell.type)) &&
  (scope.qualifiers == null || scope.qualifiers.includes(cell.qualifier)) &&
  (scope.impacts == null || scope.impacts.includes(cell.impact)) &&
  (scope.likelihoods == null || scope.likelihoods.includes(cell.likelihood)) &&
  (scope.riskSeverities == null || scope.riskSeverities.includes(cell.riskSeverity)) &&
  (scope.confidences == null || scope.confidences.includes(cell.confidence))
const key = (cell: Cell, dimension: string) =>
  ({
    client: cell.client,
    repository: cell.repository,
    model: cell.model,
    type: cell.type,
    qualifier: cell.qualifier,
    impact: cell.impact,
    likelihood: cell.likelihood,
    riskSeverity: cell.riskSeverity,
    confidence: cell.confidence,
  })[dimension as 'client'] ?? 'all'
const label = (id: string, dimension: string) =>
  ({
    client: clients,
    repository: scopes.map((scope) => ({
      id: repoId(scope),
      label: `${scope.label} · ${clients.find((client) => client.id === scope.client)?.label}`,
    })),
    model: models,
    type: types,
    qualifier: qualifiers,
    impact: riskFacets('impact'),
    likelihood: riskFacets('likelihood'),
    riskSeverity: riskFacets('riskSeverity'),
    confidence: riskFacets('confidence'),
  })[dimension as 'client']?.find((item) => item.id === id)?.label ?? 'All selected evidence'
function bucketStart(date: string, bucket: string): string {
  const value = new Date(`${date}T00:00:00Z`)
  if (bucket === 'week') value.setUTCDate(value.getUTCDate() - ((value.getUTCDay() + 6) % 7))
  if (bucket === 'month') value.setUTCDate(1)
  return value.toISOString().slice(0, 10)
}
function endOfBucket(date: string, bucket: string): string {
  const value = new Date(`${date}T00:00:00Z`)
  if (bucket === 'week') value.setUTCDate(value.getUTCDate() + 6)
  if (bucket === 'month') {
    value.setUTCMonth(value.getUTCMonth() + 1)
    value.setUTCDate(0)
  }
  return value.toISOString().slice(0, 10)
}
function dates(scope: PerformanceScope, bucket: string): string[] {
  const values: string[] = []
  const end = Date.parse(scope.to!)
  for (let day = Date.parse(scope.from!); day <= end; day += 86400000)
    values.push(bucketStart(new Date(day).toISOString().slice(0, 10), bucket))
  return [...new Set(values)]
}
function measurement(
  rows: Cell[],
  date: string,
  scope: PerformanceScope,
  query: PerformanceQuery,
  dimension: string,
  observed = true,
  exclusions: PerformanceExclusion[] = [],
  coverageRows = rows,
): PerformancePoint {
  const modelScope = scope.models != null || dimension === 'model'
  const riskScope = hasRiskScope(scope, dimension, query)
  const counts = emptyCounts()
  for (const row of rows) add(counts, row.counts)
  const sources = [...new Map(coverageRows.map((row) => [row.aggregateId, row])).values()].map((row) => {
    const members = coverageRows.filter((member) => member.aggregateId === row.aggregateId)
    const failed = members.find((member) => modelScope ? !member.modelCovered : !member.covered)
    return { ...row, covered: !failed, exclusionReason: modelScope ? failed?.modelReason : failed?.exclusionReason }
  })
  const excluded = sources.filter((row) => !row.covered)
  const coveredIds = new Set(sources.filter((row) => row.covered).map((row) => row.aggregateId))
  const coveredCounts = emptyCounts()
  for (const row of rows.filter((row) => coveredIds.has(row.aggregateId))) add(coveredCounts, row.counts)
  if (modelScope) {
    coveredCounts.actedMisses = 0
    coveredCounts.unactedMisses = 0
    coveredCounts.provisionalMisses = 0
    coveredCounts.harvestedThreads = 0
    const issues = new Map(rows.filter((row) => coveredIds.has(row.aggregateId)).flatMap((row) => row.concerns.map((concern) => [concern.id, { ...concern, source: row.aggregateId }] as const)))
    for (const concern of issues.values()) {
      const selected = new Set(rows.filter((row) => row.aggregateId === concern.source).map((row) => row.model))
      if (!concern.detectedBy.some((model) => selected.has(model))) {
        const field = concern.acted ? 'actedMisses' : 'unactedMisses'
        coveredCounts[field] = (coveredCounts[field] ?? 0) + 1
      }
      coveredCounts.harvestedThreads = (coveredCounts.harvestedThreads ?? 0) + 1
    }
  }
  const covered = sources.length - excluded.length
  const meetsMinimum = covered >= 1 && covered * 100 >= sources.length * 80
  const hasObservation = (query.aggregation === 'period' ? observed : rows.length > 0) &&
    ((counts.generated ?? 0) > 0 || (coveredCounts.harvestedThreads ?? 0) > 0)
  const result = score(coveredCounts, false)
  if (covered === 0 || riskScope) {
    result.summary = { precision: result.summary?.precision }
    result.scenarios = result.scenarios?.map((item) => ({ ...item, falseNegatives: null, recall: null, f1: null }))
  }
  const overall = score(counts, false)
  if (!hasObservation) {
    result.summary = {}
    result.scenarios = result.scenarios?.map((item) => ({
      ...item,
      precision: null,
      recall: null,
      f1: null,
    }))
  }
  if (!hasObservation) {
    overall.summary = {}
    overall.scenarios = overall.scenarios?.map((item) => ({ ...item, precision: null }))
  }
  const ids = excluded.map((row) => {
    let entry = exclusions.find((entry) => entry.aggregateId === row.aggregateId && entry.reasons?.[0] === row.exclusionReason)
    if (!entry) {
      const index = scopes.findIndex((scope) => repoId(scope) === row.repository)
      const repairRequired = row.pullRequestId % 2 === 0 && row.exclusionReason !== 'miss-judgement-failed'
      entry = {
        id: exclusions.length, aggregateId: row.aggregateId, clientId: row.client,
        clientName: clients.find((client) => client.id === row.client)?.label ?? row.client,
        repositoryId: row.repository, repositoryName: scopes[index]?.label,
        pullRequestId: row.pullRequestId, providerScope: provider,
        reasons: [row.exclusionReason ?? 'miss-collection-coverage-unavailable'],
        lastAttemptAt: `${row.date}T12:00:00Z`, lastRefreshReason: repairRequired ? 'SourceCoordinatesUnavailable' : row.exclusionReason === 'miss-judgement-failed' ? 'ClassificationUnavailable' : 'ProviderReadFailed',
        recoveryDisposition: repairRequired ? 'repair-required' : 'retryable',
        collectionEnabled: true, notBefore: `${row.date}T13:00:00Z`,
        sourceUrl: repairRequired ? undefined : `https://dev.azure.com/example/project/_git/${scopes[index]?.repo}/pullrequest/${row.pullRequestId}`,
      }
      if (modelScope) {
        entry.recoveryAction = row.exclusionReason === 'model-concern-match-unavailable' ? 'retry-model-evidence'
          : row.exclusionReason === 'model-concern-collection-unavailable' ? 'recollect-source' : 'none'
        entry.sourceConnectionId = sourceConnectionId
        entry.recoveryDisposition = entry.recoveryAction === 'none' ? 'unreconstructable-history' : 'ready'
        entry.lastModelRetryAt = modelRecovery.has(row.aggregateId) ? new Date(modelRecovery.get(row.aggregateId)!).toISOString() : undefined
        entry.modelRetryNotBefore = modelRecovery.has(row.aggregateId) ? new Date(modelRecovery.get(row.aggregateId)! + 300000).toISOString() : undefined
      }
      const progress = recoveryProgress(row.aggregateId)
      if (progress) {
        entry.recoveryDisposition = progress.state
        entry.lastRefreshReason = progress.state === 'repair-required' ? 'SourceCoordinatesUnavailable' : null
        entry.notBefore = new Date(progress.state === 'queued' ? progress.queuedAt : progress.state === 'in-progress' ? progress.started + 15 * 60000 : progress.completed + 360 * 60000).toISOString()
        if (progress.state !== 'queued') entry.lastAttemptAt = new Date(progress.started).toISOString()
      }
      exclusions.push(entry)
      recoveryCatalog.set(modelScope ? `${row.aggregateId}/${provider}/${sourceConnectionId}` : row.aggregateId, { ...entry })
    }
    return entry.id!
  })
  const exclusionReasons = [...new Set(excluded.map((row) => row.exclusionReason ?? 'miss-collection-coverage-unavailable'))]
  return {
    date,
    windowFrom:
      query.aggregation === 'period' ? (date < scope.from! ? scope.from : date) : scope.from,
    windowTo:
      endOfBucket(date, query.bucket ?? 'day') > scope.to!
        ? scope.to
        : endOfBucket(date, query.bucket ?? 'day'),
    score: result,
    overallPrecision: { counts: overall.counts, summary: overall.summary?.precision, scenarios: overall.scenarios?.map(({ id, truePositives, falsePositives, excluded, precision }) => ({ id, truePositives, falsePositives, excluded, precision })) },
    coverage: {
      candidatePullRequests: sources.length, coveredPullRequests: covered, excludedPullRequests: excluded.length,
      percentage: sources.length ? 100 * covered / sources.length : null, meetsMinimum, recallSupported: !riskScope,
      reasons: exclusionReasons.map((reason) => ({ reason, pullRequests: excluded.filter((row) => row.exclusionReason === reason).length })), exclusionIds: ids,
    },
    unavailableReasons: [
      ...(riskScope ? ['miss-risk-assessment-unavailable'] : []),
      ...exclusionReasons,
      ...(covered < 1 ? ['insufficient-covered-pull-requests'] : []),
      ...(sources.length > 0 && covered * 100 < sources.length * 80 ? ['coverage-below-minimum'] : []),
      ...(counts.provisionalMisses ? ['miss-observation-provisional'] : []),
      ...(counts.duplicateUnknown ? ['duplicate-verification-incomplete'] : []),
      ...(!hasObservation ? ['no-series-observation-in-period'] : []),
    ],
  }
}
export function mockPerformanceResponse(query: PerformanceQuery): PerformanceResponse {
  if (!query.views?.length || query.views.length > 2) throw new Error('Provide one or two views.')
  const views = query.views.map((scope, index) => {
    if (
      !scope.from ||
      !scope.to ||
      Date.parse(scope.to) - Date.parse(scope.from) > 365 * 86400000 ||
      scope.to < scope.from
    )
      throw new Error('The date window must be between 1 and 366 days.')
    const raw = cells(scope.from, scope.to)
    const selected = raw.filter((cell) => matches(cell, scope))
    const bucket = query.bucket ?? 'day'
    const grouping = query.grouping ?? 'none'
    const availableClients = clients.filter(
      (client) => scope.clientIds == null || scope.clientIds.includes(client.id),
    )
    const facets: PerformanceFacets = {
      clients: availableClients,
      repositories: scopes
        .filter((item) => availableClients.some((client) => client.id === item.client))
        .map((item) => ({ id: repoId(item), label: item.label })),
      models,
      types,
      qualifiers,
      impacts: riskFacets('impact'),
      likelihoods: riskFacets('likelihood'),
      riskSeverities: riskFacets('riskSeverity'),
      confidences: riskFacets('confidence'),
    }
    const keys =
      grouping === 'none'
        ? ['all']
        : [...new Set(selected.map((cell) => key(cell, grouping)))].sort()
    const exclusionCatalog: PerformanceExclusion[] = []
    const series = keys.map((id) => ({
      id,
      label: label(id, grouping),
      points: dates(scope, bucket).map((date) => {
        const periodEnd = endOfBucket(date, bucket)
        const rows = selected.filter(
          (cell) =>
            (grouping === 'none' || key(cell, grouping) === id) &&
            cell.date <= periodEnd &&
            (query.aggregation !== 'period' || cell.date >= date),
        )
        return measurement(
          rows,
          date,
          grouping === 'model' ? { ...scope, models: [id] } : scope,
          query,
          grouping,
          selected.some(
            (cell) =>
              (grouping === 'none' || key(cell, grouping) === id) &&
              cell.date >= date &&
              cell.date <= periodEnd,
          ),
          exclusionCatalog,
          hasRiskScope(scope, grouping, query) ? raw.filter(cell => matches(cell, withoutRisk(scope)) &&
            (grouping === 'none' || ['impact', 'likelihood', 'riskSeverity', 'confidence'].includes(grouping) || key(cell, grouping) === id) &&
            cell.date <= periodEnd && (query.aggregation !== 'period' || cell.date >= date)) : rows,
        )
      }),
    }))
    const breakdown: PerformanceMatrixCell[] = []
    const axes = query.breakdown
    if (axes && (axes.viewIndex ?? 0) === index) {
      const date = bucketStart(axes.date ?? scope.to, bucket)
      const periodEnd = endOfBucket(date, bucket)
      const rows = selected.filter(
        (cell) => cell.date <= periodEnd && (query.aggregation !== 'period' || cell.date >= date),
      )
      for (const rowId of new Set(rows.map((cell) => key(cell, axes.rows!))))
        for (const columnId of new Set(rows.map((cell) => key(cell, axes.columns!)))) {
          const intersection = rows.filter(
            (cell) => key(cell, axes.rows!) === rowId && key(cell, axes.columns!) === columnId,
          )
          const scoped = {
            ...scope,
            models:
              axes.rows === 'model' || axes.columns === 'model'
                ? [axes.rows === 'model' ? rowId : columnId]
                : scope.models,
          }
          for (const [axis, id] of [[axes.rows, rowId], [axes.columns, columnId]]) {
            const selection = { impact: 'impacts', likelihood: 'likelihoods', riskSeverity: 'riskSeverities', confidence: 'confidences' }[axis ?? '']
            if (selection) Object.assign(scoped, { [selection]: [id] })
          }
          breakdown.push({
            rowId,
            rowLabel: label(rowId, axes.rows!),
            columnId,
            columnLabel: label(columnId, axes.columns!),
            measurement: measurement(
              intersection,
              date,
              scoped,
              query,
              grouping,
              intersection.length > 0,
              exclusionCatalog,
              hasRiskScope(scoped, grouping, query) ? raw.filter(cell => matches(cell, withoutRisk(scoped)) &&
                (['impact', 'likelihood', 'riskSeverity', 'confidence'].includes(axes.rows!) || key(cell, axes.rows!) === rowId) &&
                (['impact', 'likelihood', 'riskSeverity', 'confidence'].includes(axes.columns!) || key(cell, axes.columns!) === columnId) &&
                cell.date <= periodEnd && (query.aggregation !== 'period' || cell.date >= date)) : intersection,
            ),
          })
        }
    }
    return {
      scope: { ...scope, clientIds: availableClients.map((client) => client.id) },
      facets,
      series,
      breakdown,
      aggregateCells: selected.length,
      exclusionCatalog,
      evidence: {
        oldestProjectionAt: new Date().toISOString(),
        newestProjectionAt: new Date().toISOString(),
        projectionVersion: 6,
        modelEvidenceSchema: scope.models != null || grouping === 'model' || axes?.rows === 'model' || axes?.columns === 'model' ? 'model-opportunity-v1' : undefined,
        pendingSourceAggregates: 0,
      },
    }
  })
  return {
    calculationVersion: version,
    capturedAt: new Date().toISOString(),
    evidenceRevision: 'mock-retained-population-v1',
    coveragePolicy: { minimumCoveragePercentage: 80, minimumCoveredPullRequests: 1 },
    query,
    premiseIds: score(emptyCounts(), false).scenarios?.map((item) => item.id!),
    views,
  }
}
const reports = new Map<string, { request: string; report: PerformanceSavedReport }>()
export function reviewerPerformanceHandlers(base: string) {
  return [
    http.post(`${base}/reviewer-performance/evidence/:aggregateId/retry-model`, async ({ params, request }) => {
      await delay(80)
      const source = await request.json() as { providerScope?: string; connectionId?: string }
      const id = params.aggregateId as string
      const entry = recoveryCatalog.get(`${id}/${source.providerScope}/${source.connectionId}`)
      if (!entry) return new HttpResponse(null, { status: 404 })
      if (entry.recoveryAction !== 'retry-model-evidence') return HttpResponse.json({ detail: 'Historical model evidence cannot be reconstructed by this action.' }, { status: 409 })
      if (modelRecovery.has(id) && Date.now() < modelRecovery.get(id)! + 300000) return HttpResponse.json({ detail: 'Wait until the next classification attempt is permitted, then retry.' }, { status: 409 })
      modelRecovery.set(id, Date.now())
      return new HttpResponse(null, { status: 200 })
    }),
    http.post(`${base}/reviewer-performance/evidence/:aggregateId/retry`, async ({ params }) => {
      await delay(80)
      const id = params.aggregateId as string
      const entry = recoveryCatalog.get(id)
      if (!entry) return new HttpResponse(null, { status: 404 })
      if (entry.collectionEnabled === false) return HttpResponse.json({ title: 'Evidence collection is disabled', detail: 'Enable Code Insights collection for this client, then retry.' }, { status: 409 })
      if (['queued', 'in-progress'].includes(recoveryProgress(id)?.state ?? '')) return HttpResponse.json({ title: 'Evidence observation is in progress', detail: 'Wait for the current evidence observation to finish, then retry.' }, { status: 409 })
      queuedRecovery.set(id, { queuedAt: Date.now(), requiresRepair: entry.lastRefreshReason === 'SourceCoordinatesUnavailable' })
      return new HttpResponse(null, { status: 202 })
    }),
    http.post(`${base}/reviewer-performance/ranges/query`, async ({ request }) => {
      await delay(80)
      try {
        return HttpResponse.json(
          mockPerformanceResponse((await request.json()) as PerformanceQuery),
        )
      } catch (error) {
        return HttpResponse.json({ detail: (error as Error).message }, { status: 400 })
      }
    }),
    http.get(`${base}/reviewer-performance/reports`, () =>
      HttpResponse.json([...reports.values()].map((item) => item.report.report)),
    ),
    http.post(`${base}/reviewer-performance/reports`, async ({ request }) => {
      const body =
        (await request.json()) as components['schemas']['ReviewerPerformanceSaveReportRequest']
      const requestKey = JSON.stringify(body, (key, value: unknown) => key === 'confidences' && value === null ? undefined : value)
      const existing = reports.get(body.id!)
      if (existing)
        return existing.request === requestKey
          ? HttpResponse.json(existing.report)
          : HttpResponse.json(
              { detail: 'The identifier belongs to another report.' },
              { status: 409 },
            )
      try {
        const response = mockPerformanceResponse(body.query!)
        if (!response.views?.some((view) => view.scope?.clientIds?.length))
          throw new Error('A saved report must include at least one authorized client.')
        const now = new Date()
        const report = {
          report: {
            id: body.id,
            name: body.name,
            calculationVersion: version,
            capturedAt: now.toISOString(),
            expiresAt: new Date(now.getTime() + 365 * 86400000).toISOString(),
          },
          response,
          compatibleVersion: true,
        }
        reports.set(body.id!, { request: requestKey, report: structuredClone(report) })
        return HttpResponse.json(report)
      } catch (error) {
        return HttpResponse.json({ detail: (error as Error).message }, { status: 400 })
      }
    }),
    http.get(`${base}/reviewer-performance/reports/:id`, ({ params }) => {
      const stored = reports.get(params.id as string)
      return stored ? HttpResponse.json(stored.report) : new HttpResponse(null, { status: 404 })
    }),
    http.delete(
      `${base}/reviewer-performance/reports/:id`,
      ({ params }) =>
        new HttpResponse(null, { status: reports.delete(params.id as string) ? 204 : 404 }),
    ),
  ]
}
