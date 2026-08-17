// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { http, HttpResponse, delay } from 'msw'
import protocolMockData from '../../mock/data/protocol_response_1.json'
import { API_BASE_URL } from '@/services/apiBase'

const base = API_BASE_URL

const tenantSsoCapabilityKey = 'sso-authentication'
const mockLicensingStateKey = 'mock-licensing-state'

let mockEdition = 'commercial'
let mockSsoCapabilityAvailable = true

hydrateMockLicensingState()

let mockTenants = [
  {
    id: 'tenant-1',
    slug: 'acme',
    displayName: 'Acme Corp',
    isActive: true,
    localLoginEnabled: true,
    createdAt: '2026-04-24T12:00:00Z',
    updatedAt: '2026-04-24T12:00:00Z',
    // Empty means unrestricted, which is the same reading the server applies. Held here so the allow-list
    // editor round-trips in the mock instead of appearing to save and then reverting.
    allowedAiProviderKinds: [] as string[],
    allowedAiEndpointHosts: [] as string[],
  },
]

let mockTenantSsoProviders: Record<string, any[]> = {
  'tenant-1': [
    {
      id: 'provider-1',
      tenantId: 'tenant-1',
      displayName: 'Acme Entra',
      providerKind: 'EntraId',
      protocolKind: 'Oidc',
      issuerOrAuthorityUrl: 'https://identity.example.test/acme',
      clientId: 'acme-client-id',
      secretConfigured: true,
      scopes: ['openid', 'profile', 'email'],
      allowedEmailDomains: ['acme.test'],
      isEnabled: true,
      autoCreateUsers: true,
      createdAt: '2026-04-24T12:00:00Z',
      updatedAt: '2026-04-24T12:00:00Z',
    },
  ],
}

function getMockSsoCapability() {
  return {
    key: tenantSsoCapabilityKey,
    displayName: 'Single sign-on authentication',
    requiresCommercial: true,
    defaultWhenCommercial: true,
    overrideState: 'default',
    isAvailable: mockSsoCapabilityAvailable,
    message: mockSsoCapabilityAvailable ? null : 'A commercial license is required to use single sign-on, including in self-hosted deployments.',
  }
}

function getMockBudgetingCapability() {
  return {
    key: 'budgeting',
    displayName: 'Budgeting',
    requiresCommercial: true,
    defaultWhenCommercial: true,
    overrideState: 'default',
    isAvailable: true,
    message: null,
  }
}

function getMockCodeInsightsCapability() {
  return {
    key: 'code-insights',
    displayName: 'Code Insights',
    requiresCommercial: true,
    defaultWhenCommercial: true,
    overrideState: 'default',
    isAvailable: true,
    message: null,
  }
}

function getMockMentionAnsweringCapability() {
  return {
    key: 'mention-answering',
    displayName: 'Mention answering',
    requiresCommercial: true,
    defaultWhenCommercial: true,
    overrideState: 'default',
    isAvailable: true,
    message: null,
  }
}

function getMockTenantBySlug(tenantSlug: string) {
  return mockTenants.find((tenant) => tenant.slug === tenantSlug) ?? null
}

function getMockTenantById(tenantId: string) {
  return mockTenants.find((tenant) => tenant.id === tenantId) ?? null
}

function createPremiumFeatureUnavailableResponse() {
  return HttpResponse.json(
    {
      error: 'premium_feature_unavailable',
      feature: tenantSsoCapabilityKey,
      message: 'A commercial license is required to use single sign-on, including in self-hosted deployments.',
    },
    { status: 409 },
  )
}

function persistMockLicensingState() {
  if (typeof window === 'undefined') {
    return
  }

  window.localStorage.setItem(mockLicensingStateKey, JSON.stringify({
    edition: mockEdition,
    ssoAvailable: mockSsoCapabilityAvailable,
  }))
}

function hydrateMockLicensingState() {
  if (typeof window === 'undefined') {
    return
  }

  try {
    const rawValue = window.localStorage.getItem(mockLicensingStateKey)
    if (!rawValue) {
      return
    }

    const parsed = JSON.parse(rawValue) as {
      edition?: string
      ssoAvailable?: boolean
    }

    if (parsed.edition === 'community' || parsed.edition === 'commercial') {
      mockEdition = parsed.edition
    }

    if (typeof parsed.ssoAvailable === 'boolean') {
      mockSsoCapabilityAvailable = parsed.ssoAvailable
    }
  } catch {
    // Ignore invalid persisted mock state and keep defaults.
  }
}

function decodeBase64UrlSegment(segment: string): string | null {
  if (!segment) {
    return null
  }

  const normalized = segment.replaceAll('-', '+').replaceAll('_', '/')
  const paddingLength = (4 - (normalized.length % 4)) % 4
  const base64 = normalized.padEnd(normalized.length + paddingLength, '=')

  try {
    const binary = atob(base64)
    const bytes = Uint8Array.from(binary, (character) => character.codePointAt(0) ?? 0)
    return new TextDecoder().decode(bytes)
  } catch {
    return null
  }
}

function encodeBase64Url(value: string): string {
  return btoa(value)
    .replaceAll('+', '-')
    .replaceAll('/', '_')
    .replaceAll(/=+$/g, '')
}

function createMockJwt(payload: { global_role: string; unique_name: string }): string {
  const header = encodeBase64Url(JSON.stringify({ alg: 'none', typ: 'JWT' }))
  const body = encodeBase64Url(JSON.stringify({
    ...payload,
    exp: Math.floor(Date.now() / 1000) + 3600,
    probe: 'a~',
  }))

  return `${header}.${body}.dummySignature`
}

const mockAdminAccessToken = createMockJwt({ global_role: 'Admin', unique_name: 'mock.admin' })
const mockTenantAccessToken = createMockJwt({ global_role: 'User', unique_name: 'tenant.user' })

// Mock session cookie helpers — a real (non-httpOnly) document.cookie so the mock can demonstrate
// cross-tab session sharing the way the real httpOnly backend cookie does.
const MOCK_SESSION_COOKIE = 'meisterpropr_refresh'
function setMockSessionCookie(value: 'admin' | 'tenant' | 'sso'): void {
  document.cookie = `${MOCK_SESSION_COOKIE}=${value}; path=/; samesite=lax`
}
function clearMockSessionCookie(): void {
  document.cookie = `${MOCK_SESSION_COOKIE}=; path=/; max-age=0`
}
function readMockSessionCookie(): string | null {
  const match = document.cookie.match(new RegExp(`(?:^|;\\s*)${MOCK_SESSION_COOKIE}=([^;]*)`))
  return match ? match[1] : null
}
const mockTenantSsoAccessToken = createMockJwt({ global_role: 'User', unique_name: 'tenant.sso.user' })

function parseJwtPayload(authorizationHeader: string | null) {
  if (!authorizationHeader?.startsWith('Bearer ')) {
    return null
  }

  const token = authorizationHeader.slice('Bearer '.length)
  const payloadJson = decodeBase64UrlSegment(token.split('.')[1] ?? '')
  if (!payloadJson) {
    return null
  }

  try {
    return JSON.parse(payloadJson) as {
      global_role?: string
      unique_name?: string
    }
  } catch {
    return null
  }
}

let jobTick = 0

// Generates 25 passes × 40 events for stress-testing the protocol trace view
function generateLargeReviewProtocols() {
    const JOB_ID = 'job-large'
    const FILES = [
        'src/components/UserProfile.vue', 'src/components/Dashboard.vue', 'src/components/Settings.vue',
        'src/stores/authStore.ts', 'src/stores/userStore.ts', 'src/stores/notificationStore.ts',
        'src/services/apiClient.ts', 'src/services/authService.ts', 'src/services/userService.ts',
        'src/utils/validation.ts', 'src/utils/formatting.ts', 'src/utils/dateHelpers.ts',
        'src/router/index.ts', 'src/router/guards.ts',
        'src/composables/useAuth.ts', 'src/composables/useForm.ts', 'src/composables/usePagination.ts',
        'backend/api/controllers/UserController.cs', 'backend/api/controllers/AuthController.cs',
        'backend/services/UserService.cs', 'backend/services/TokenService.cs',
        'backend/repositories/UserRepository.cs', 'backend/repositories/AuditRepository.cs',
        'backend/infrastructure/Database.cs', 'backend/infrastructure/CacheService.cs',
    ]
    const TOOL_NAMES = ['get_file_content', 'search_codebase', 'read_symbol', 'list_directory', 'get_git_diff', 'search_by_name']
    const OUTCOMES = ['Completed', 'Completed', 'Completed', 'Completed', 'Warning']

    const baseTime = new Date('2026-06-12T18:00:00Z').getTime()

    return FILES.map((file, passIdx) => {
        const passStart = baseTime + passIdx * 90_000
        const events: any[] = []
        let eventTime = passStart + 1000
        let eventIdx = 0

        // 5 AI iterations, each with 1 aiCall + 7 tool calls = 40 events per pass
        for (let iter = 1; iter <= 5; iter++) {
            events.push({
                id: `pass${passIdx}-iter${iter}-ai`,
                kind: 'aiCall',
                eventCategory: 'ai-call',
                name: `ai_call_iter_${iter}`,
                occurredAt: new Date(eventTime).toISOString(),
                durationMs: 3200 + Math.floor((passIdx * 17 + iter * 31) % 2800),
                inputTokens: 1800 + (iter * 120) + (passIdx * 37),
                outputTokens: 40 + (iter * 8),
                inputTextSample: `Reviewing ${file} (pass ${passIdx + 1} of ${FILES.length}). Iteration ${iter}. Checking for correctness, security, and style issues.`,
                outputSummary: iter < 5 ? `Identified ${iter} potential issues. Requesting additional context.` : `Review complete for ${file}. Found ${passIdx % 4} issues.`,
                error: null,
            })
            eventTime += 3500
            eventIdx++

            for (let t = 0; t < 7; t++) {
                const toolName = TOOL_NAMES[(passIdx * 7 + iter * 3 + t) % TOOL_NAMES.length]
                events.push({
                    id: `pass${passIdx}-iter${iter}-tool${t}`,
                    kind: 'toolCall',
                    eventCategory: 'tool-call',
                    name: toolName,
                    occurredAt: new Date(eventTime).toISOString(),
                    durationMs: 120 + ((passIdx + t) * 13) % 400,
                    inputTokens: null,
                    outputTokens: null,
                    inputTextSample: `{"path":"${file}","offset":${t * 50}}`,
                    outputSummary: `Tool result for ${toolName} on ${file} line ${t * 20 + 1}.`,
                    error: null,
                })
                eventTime += 200
                eventIdx++
            }
        }

        const outcome = OUTCOMES[passIdx % OUTCOMES.length]
        return {
            id: `large-pass-${passIdx}`,
            jobId: JOB_ID,
            attemptNumber: 1,
            label: file,
            fileResultId: `large-result-${passIdx}`,
            startedAt: new Date(passStart).toISOString(),
            completedAt: new Date(passStart + 85_000).toISOString(),
            outcome,
            totalInputTokens: events.filter(e => e.kind === 'aiCall').reduce((s, e) => s + (e.inputTokens ?? 0), 0),
            totalOutputTokens: events.filter(e => e.kind === 'aiCall').reduce((s, e) => s + (e.outputTokens ?? 0), 0),
            iterationCount: 5,
            toolCallCount: 35,
            finalConfidence: 70 + (passIdx % 30),
            events,
        }
    })
}

const reviewProfiles = [
  { profileId: 'file-by-file-calm', displayName: 'Calm', isDefault: false },
  { profileId: 'file-by-file-balanced', displayName: 'Balanced', isDefault: true },
  { profileId: 'file-by-file-assertive', displayName: 'Assertive', isDefault: false },
]

const clientReviewProfiles: Record<string, { defaultReviewPipelineProfileId: string | null; updatedAtUtc: string | null }> = {
  '1': { defaultReviewPipelineProfileId: 'file-by-file-balanced', updatedAtUtc: null },
  '2': { defaultReviewPipelineProfileId: null, updatedAtUtc: null },
  '3': { defaultReviewPipelineProfileId: 'file-by-file-assertive', updatedAtUtc: new Date().toISOString() },
}

function getEffectiveReviewProfile(clientId: string) {
  const stored = clientReviewProfiles[clientId]?.defaultReviewPipelineProfileId ?? null
  return {
    clientId,
    defaultReviewPipelineProfileId: stored ?? 'file-by-file-balanced',
    source: stored ? 'clientDefault' : 'systemDefault',
    updatedAtUtc: clientReviewProfiles[clientId]?.updatedAtUtc ?? null,
  }
}

// Fields a PATCH set on a client, kept so a saved review-pass list or toggle is still there after a reload.
// Without this the mock accepted a save and answered the next GET as though it had never happened, which looks
// exactly like the bug where saving review passes left the list empty.
const patchedClientFields: Record<string, Record<string, unknown>> = {}

function buildMockClient(id: string, displayName = `Mocked Client ${id}`, overrides: Record<string, unknown> = {}) {
  const storedProfile = clientReviewProfiles[id]?.defaultReviewPipelineProfileId ?? null
  overrides = { ...(patchedClientFields[id] ?? {}), ...overrides }
  return {
    id,
    displayName,
    isActive: true,
    createdAt: new Date().toISOString(),
    recentUsageTokens: 14520,
    reviewerId: '0000-1111-2222-3333',
    defaultReviewPipelineProfileId: storedProfile,
    defaultReviewPipelineProfileUpdatedAtUtc: clientReviewProfiles[id]?.updatedAtUtc ?? null,
    scmCommentPostingEnabled: true,
    enableEvidenceBackedVerification: false,
    enableMultiPassUnion: false,
    reviewEveryIncrementEnabled: false,
    withholdOutOfScopeFindings: false,
    enableLanguageRobustScreening: false,
    outputLanguage: 'en',
    // Client "3" (Umbrella) is intentionally uncapped to exercise the no-budget state; others carry caps.
    budgetConfig: id === '3'
      ? null
      : {
        monthlySoftCapUsd: 80,
        monthlyHardCapUsd: 100,
        pullRequestSoftCapUsd: null,
        pullRequestHardCapUsd: null,
        incrementSoftCapUsd: null,
        incrementHardCapUsd: null,
      },
    ...overrides,
  }
}

function pad2(value: number): string {
  return String(value).padStart(2, '0')
}

// Client "3" (Umbrella) has no caps to exercise the no-budget state; others carry monthly caps.
function budgetCapsForClient(clientId: string) {
  const noBudget = clientId === '3'
  return { monthlySoftCapUsd: noBudget ? null : 80, monthlyHardCapUsd: noBudget ? null : 100 }
}

interface MockSpendReset {
  id: string
  periodStart: string
  topUpSoftCapUsd: number | null
  topUpHardCapUsd: number | null
  effectiveSoftCapBeforeUsd: number | null
  effectiveSoftCapAfterUsd: number | null
  effectiveHardCapBeforeUsd: number | null
  effectiveHardCapAfterUsd: number | null
  actorUserId: string | null
  actorUsername: string | null
  performedAt: string
}

// Manual spend resets granted during a mock session, keyed by `clientId|YYYY-MM`. Mutable so the UI can be driven
// end-to-end: granting one raises the effective caps every budget payload reports, as the real store does.
const mockSpendResets = new Map<string, MockSpendReset[]>()

function resetKey(clientId: string, year: number, month: number): string {
  return `${clientId}|${year}-${pad2(month)}`
}

function mockResetsFor(clientId: string, year: number, month: number): MockSpendReset[] {
  return mockSpendResets.get(resetKey(clientId, year, month)) ?? []
}

/** Totals a period's granted allowance, mirroring the server-side cumulative rule. */
function mockTopUpFor(clientId: string, year: number, month: number) {
  return mockResetsFor(clientId, year, month).reduce(
    (total, reset) => ({
      soft: total.soft + (reset.topUpSoftCapUsd ?? 0),
      hard: total.hard + (reset.topUpHardCapUsd ?? 0),
    }),
    { soft: 0, hard: 0 },
  )
}

/** An unset cap stays unset — "no limit" cannot be topped up. */
function applyMockTopUp(configuredCap: number | null, topUpUsd: number): number | null {
  return configuredCap === null ? null : configuredCap + topUpUsd
}

/** Sums two client caps for a tenant total, treating "uncapped" as absent rather than zero. */
function addCap(running: number | null, next: number | null): number | null {
  return next === null ? running : (running ?? 0) + next
}

/** Grants the client's current period a fresh allowance equal to its configured caps. */
function grantMockSpendReset(clientId: string): MockSpendReset | null {
  const configured = budgetCapsForClient(clientId)
  // Mirrors the server: a cap of zero is configured but grants nothing, so it is refused like an absent cap.
  if (!configured.monthlySoftCapUsd && !configured.monthlyHardCapUsd) {
    return null
  }

  const now = new Date()
  const year = now.getUTCFullYear()
  const month = now.getUTCMonth() + 1
  const before = mockTopUpFor(clientId, year, month)
  const softBefore = applyMockTopUp(configured.monthlySoftCapUsd, before.soft)
  const hardBefore = applyMockTopUp(configured.monthlyHardCapUsd, before.hard)

  const reset: MockSpendReset = {
    id: `reset-${clientId}-${mockResetsFor(clientId, year, month).length + 1}`,
    periodStart: `${year}-${pad2(month)}-01`,
    topUpSoftCapUsd: configured.monthlySoftCapUsd,
    topUpHardCapUsd: configured.monthlyHardCapUsd,
    effectiveSoftCapBeforeUsd: softBefore,
    effectiveSoftCapAfterUsd: softBefore === null ? null : softBefore + (configured.monthlySoftCapUsd ?? 0),
    effectiveHardCapBeforeUsd: hardBefore,
    effectiveHardCapAfterUsd: hardBefore === null ? null : hardBefore + (configured.monthlyHardCapUsd ?? 0),
    actorUserId: 'mock-admin',
    actorUsername: 'saen',
    performedAt: now.toISOString(),
  }

  const key = resetKey(clientId, year, month)
  mockSpendResets.set(key, [...(mockSpendResets.get(key) ?? []), reset])
  return reset
}

// Builds a representative budget-consumption payload for a period (the current month by default). Dates track the
// real calendar so the picker + forecast read sensibly on any day; a past month returns full-month actuals with no
// forecast.
function mockSpentToDate(
  elapsedDays: number,
  projectedTargetUsd: number,
  isCurrent: boolean,
  month: number,
  daysInMonth: number,
): number {
  if (elapsedDays === 0) {
    return 0
  }

  if (isCurrent) {
    return Math.round(((projectedTargetUsd * elapsedDays) / daysInMonth) * 100) / 100
  }

  return Math.round((40 + ((month * 7) % 55)) * 100) / 100
}

function buildMockBudgetConsumption(clientId: string, period?: string | null) {
  const now = new Date()
  const currentYear = now.getUTCFullYear()
  const currentMonth = now.getUTCMonth() + 1

  let year = currentYear
  let month = currentMonth
  if (period) {
    const [parsedYear, parsedMonth] = period.split('-').map(Number)
    if (Number.isFinite(parsedYear) && Number.isFinite(parsedMonth)) {
      year = parsedYear
      month = parsedMonth
    }
  }

  const daysInMonth = new Date(Date.UTC(year, month, 0)).getUTCDate()
  const monthLabel = pad2(month)
  const isCurrent = year === currentYear && month === currentMonth
  const isPast = year < currentYear || (year === currentYear && month < currentMonth)
  const { monthlySoftCapUsd: configuredSoftCapUsd, monthlyHardCapUsd: configuredHardCapUsd } =
    budgetCapsForClient(clientId)
  // The caps reported are the ones in force for THIS period: configured plus whatever its resets granted.
  const periodResets = mockResetsFor(clientId, year, month)
  const topUp = mockTopUpFor(clientId, year, month)
  const monthlySoftCapUsd = applyMockTopUp(configuredSoftCapUsd, topUp.soft)
  const monthlyHardCapUsd = applyMockTopUp(configuredHardCapUsd, topUp.hard)
  const noBudget = clientId === '3'

  const elapsedDays = isCurrent ? now.getUTCDate() : isPast ? daysInMonth : 0
  const projectedTargetUsd = noBudget ? 55 : 90
  const spentToDateUsd = mockSpentToDate(elapsedDays, projectedTargetUsd, isCurrent, month, daysInMonth)

  const weights = Array.from({ length: elapsedDays }, (_, i) => 1 + (i % 4) * 0.5)
  const weightSum = weights.reduce((sum, w) => sum + w, 0) || 1
  let allocated = 0
  const dailySpend = weights.map((weight, i) => {
    const isLast = i === elapsedDays - 1
    const amount = isLast
      ? Math.round((spentToDateUsd - allocated) * 100) / 100
      : Math.round(spentToDateUsd * (weight / weightSum) * 100) / 100
    if (!isLast) {
      allocated += amount
    }
    return { date: `${year}-${monthLabel}-${pad2(i + 1)}`, spentUsd: Math.max(0, amount) }
  })

  return {
    clientId,
    periodStart: `${year}-${monthLabel}-01`,
    periodEnd: `${year}-${monthLabel}-${pad2(daysInMonth)}`,
    nextResetOn: month === 12 ? `${year + 1}-01-01` : `${year}-${pad2(month + 1)}-01`,
    asOf: isCurrent ? `${year}-${monthLabel}-${pad2(elapsedDays)}` : `${year}-${monthLabel}-${pad2(daysInMonth)}`,
    spentToDateUsd,
    spendIsApproximate: false,
    monthlySoftCapUsd,
    monthlyHardCapUsd,
    projectedPeriodSpendUsd: isCurrent
      ? Math.round(((spentToDateUsd / Math.max(elapsedDays, 1)) * daysInMonth) * 100) / 100
      : null,
    dailySpend,
    resets: periodResets,
    configuredSoftCapUsd,
    configuredHardCapUsd,
  }
}

// Builds a trailing-window monthly spend history; the last month is the current (partial) month.
function buildMockBudgetHistory(clientId: string, months = 12) {
  const now = new Date()
  const { monthlySoftCapUsd, monthlyHardCapUsd } = budgetCapsForClient(clientId)
  const clamped = Math.min(Math.max(months, 1), 24)
  const entries = []
  for (let offset = clamped - 1; offset >= 0; offset -= 1) {
    const monthStart = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - offset, 1))
    const year = monthStart.getUTCFullYear()
    const month = monthStart.getUTCMonth() + 1
    const fullMonth = Math.round((35 + ((month * 11) % 55)) * 100) / 100
    const isCurrent = offset === 0
    const daysInMonth = new Date(Date.UTC(year, month, 0)).getUTCDate()
    const spentUsd = isCurrent
      ? Math.round((fullMonth * (now.getUTCDate() / daysInMonth)) * 100) / 100
      : fullMonth
    const topUp = mockTopUpFor(clientId, year, month)
    entries.push({
      year,
      month,
      periodStart: `${year}-${pad2(month)}-01`,
      spentUsd,
      spendIsApproximate: false,
      // Each month carries the cap that was in force for it, so a reset month steps up in the trend chart.
      effectiveSoftCapUsd: applyMockTopUp(monthlySoftCapUsd, topUp.soft),
      effectiveHardCapUsd: applyMockTopUp(monthlyHardCapUsd, topUp.hard),
      resetCount: mockResetsFor(clientId, year, month).length,
    })
  }
  return { clientId, monthlySoftCapUsd, monthlyHardCapUsd, months: entries }
}

// Builds a tenant-wide overview reusing each mock client's current-period consumption, ordered by spend desc.
function buildMockTenantBudgetOverview(tenantId: string) {
  const now = new Date()
  const year = now.getUTCFullYear()
  const month = now.getUTCMonth() + 1
  const daysInMonth = new Date(Date.UTC(year, month, 0)).getUTCDate()
  const names: Record<string, string> = { '1': 'Acme Corp', '2': 'Globex Inc', '3': 'Umbrella Corp' }

  const clients = ['1', '2', '3']
    .map((id) => {
      const consumption = buildMockBudgetConsumption(id)
      return {
        clientId: id,
        displayName: names[id] ?? `Mocked Client ${id}`,
        spentToDateUsd: consumption.spentToDateUsd,
        monthlySoftCapUsd: consumption.monthlySoftCapUsd,
        monthlyHardCapUsd: consumption.monthlyHardCapUsd,
        projectedPeriodSpendUsd: consumption.projectedPeriodSpendUsd,
        resetCount: mockResetsFor(id, year, month).length,
      }
    })
    .sort((a, b) => b.spentToDateUsd - a.spentToDateUsd)

  return {
    tenantId,
    periodStart: `${year}-${pad2(month)}-01`,
    periodEnd: `${year}-${pad2(month)}-${pad2(daysInMonth)}`,
    asOf: `${year}-${pad2(month)}-${pad2(now.getUTCDate())}`,
    clients,
  }
}

// Builds the tenant-wide aggregate spend view: a trailing per-month trend that sums each client's history
// month-by-month, with the current-period spend-to-date and projection derived from that same current-month
// bucket — mirroring the real service, where both read the one current-month cost rollup and cannot diverge.
function buildMockTenantSpend(tenantId: string, months = 12) {
  const now = new Date()
  const year = now.getUTCFullYear()
  const month = now.getUTCMonth() + 1
  const daysInMonth = new Date(Date.UTC(year, month, 0)).getUTCDate()
  const day = now.getUTCDate()
  const clientIds = ['1', '2', '3']
  const clamped = Math.min(Math.max(months, 1), 24)

  let softCap = 0
  let hardCap = 0
  let anySoft = false
  let anyHard = false
  const trend = new Map<string, {
    year: number
    month: number
    periodStart: string
    spentUsd: number
    effectiveSoftCapUsd: number | null
    effectiveHardCapUsd: number | null
    resetCount: number
  }>()

  let resetCount = 0
  for (const id of clientIds) {
    // Sum the caps in force this period, so a client's granted allowance is visible in the tenant total.
    const configured = budgetCapsForClient(id)
    const topUp = mockTopUpFor(id, year, month)
    const caps = {
      monthlySoftCapUsd: applyMockTopUp(configured.monthlySoftCapUsd, topUp.soft),
      monthlyHardCapUsd: applyMockTopUp(configured.monthlyHardCapUsd, topUp.hard),
    }
    resetCount += mockResetsFor(id, year, month).length
    if (caps.monthlySoftCapUsd != null) {
      softCap += caps.monthlySoftCapUsd
      anySoft = true
    }
    if (caps.monthlyHardCapUsd != null) {
      hardCap += caps.monthlyHardCapUsd
      anyHard = true
    }

    for (const m of buildMockBudgetHistory(id, clamped).months) {
      const key = `${m.year}-${pad2(m.month)}`
      // Each month sums the caps in force for THAT month, so past months are not drawn against today's ceiling.
      const monthTopUp = mockTopUpFor(id, m.year, m.month)
      const monthSoft = applyMockTopUp(configured.monthlySoftCapUsd, monthTopUp.soft)
      const monthHard = applyMockTopUp(configured.monthlyHardCapUsd, monthTopUp.hard)
      const monthResets = mockResetsFor(id, m.year, m.month).length
      const existing = trend.get(key)
      if (existing) {
        existing.spentUsd = Math.round((existing.spentUsd + m.spentUsd) * 100) / 100
        existing.effectiveSoftCapUsd = addCap(existing.effectiveSoftCapUsd, monthSoft)
        existing.effectiveHardCapUsd = addCap(existing.effectiveHardCapUsd, monthHard)
        existing.resetCount += monthResets
      } else {
        trend.set(key, {
          year: m.year,
          month: m.month,
          periodStart: m.periodStart,
          spentUsd: m.spentUsd,
          effectiveSoftCapUsd: monthSoft,
          effectiveHardCapUsd: monthHard,
          resetCount: monthResets,
        })
      }
    }
  }

  const spentToDateUsd = trend.get(`${year}-${pad2(month)}`)?.spentUsd ?? 0
  const currentResets = clientIds.flatMap((id) => mockResetsFor(id, year, month))
  const lastResetAt = currentResets.length === 0
    ? null
    : currentResets.map((reset) => reset.performedAt).sort((a, b) => a.localeCompare(b)).at(-1) ?? null
  const projectedPeriodSpendUsd = Math.round(((spentToDateUsd / Math.max(day, 1)) * daysInMonth) * 100) / 100

  return {
    tenantId,
    periodStart: `${year}-${pad2(month)}-01`,
    periodEnd: `${year}-${pad2(month)}-${pad2(daysInMonth)}`,
    asOf: `${year}-${pad2(month)}-${pad2(day)}`,
    spentToDateUsd,
    monthlySoftCapUsd: anySoft ? Math.round(softCap * 100) / 100 : null,
    monthlyHardCapUsd: anyHard ? Math.round(hardCap * 100) / 100 : null,
    projectedPeriodSpendUsd,
    months: [...trend.values()].sort((a, b) => a.year - b.year || a.month - b.month),
    resetCount,
    lastResetAt: lastResetAt,
  }
}

function projectTraceSearchMatches() {
  return (protocolMockData as Array<Record<string, any>>)
    .flatMap((protocol) => (protocol.events ?? []).map((event: Record<string, any>) => ({ protocol, event })))
    .filter(({ event }) => typeof event.id === 'string')
    .map(({ protocol, event }) => ({
      jobId: String(protocol.jobId ?? ''),
      protocolId: String(protocol.id ?? ''),
      eventId: String(event.id),
      pullRequestId: 42,
      protocolLabel: typeof protocol.label === 'string' ? protocol.label : null,
      filePath: typeof protocol.label === 'string' ? protocol.label : null,
      eventKind: typeof event.kind === 'string' ? event.kind : null,
      eventCategory: typeof event.eventCategory === 'string' ? event.eventCategory : null,
      eventName: typeof event.name === 'string' ? event.name : 'unknown_event',
      modelId: 'gpt-5.4-mini',
      occurredAt: typeof event.occurredAt === 'string' ? event.occurredAt : null,
      matchedField: 'inputTextSample',
      matchSnippet: String(event.inputTextSample ?? event.outputSummary ?? '').slice(0, 240),
      contextSnippet: typeof event.outputSummary === 'string' && event.outputSummary.length > 0
        ? event.outputSummary.slice(0, 240)
        : null,
      isRedacted: false,
      hasLimitedMetadata: false,
      focus: {
        clientId: '1',
        jobId: String(protocol.jobId ?? ''),
        protocolId: String(protocol.id ?? ''),
        eventId: String(event.id),
        routeName: 'job-protocol',
        isContextAvailable: true,
        unavailableReason: null,
      },
      limitations: [],
    }))
}

let crawlConfigs = [
  {
    id: 'config-1',
    clientId: '1',
    organizationScopeId: 'scope-1',
    providerScopePath: 'https://dev.azure.com/meister-propr',
    providerProjectKey: 'Meister-ProPR',
    crawlIntervalSeconds: 60,
    isActive: true,
    repoFilters: [
      {
        id: 'filter-1',
        repositoryName: 'meister-propr',
        displayName: 'meister-propr',
        canonicalSourceRef: {
          provider: 'azureDevOps',
          value: 'repo-1',
        },
        targetBranchPatterns: ['main'],
      },
      {
        id: 'filter-2',
        repositoryName: 'propr-admin-ui',
        displayName: 'propr-admin-ui',
        canonicalSourceRef: {
          provider: 'azureDevOps',
          value: 'repo-2',
        },
        targetBranchPatterns: ['main', 'develop'],
      },
    ],
    proCursorSourceScopeMode: 'selectedSources',
    proCursorSourceIds: ['src-1', 'src-2'],
    invalidProCursorSourceIds: [],
    createdAt: '2024-03-27T10:00:00Z',
    updatedAt: '2024-03-27T10:00:00Z'
  },
  {
    id: 'config-2',
    clientId: '2',
    organizationScopeId: 'scope-3',
    providerScopePath: 'https://dev.azure.com/cloud-native',
    providerProjectKey: 'Infrastructure',
    crawlIntervalSeconds: 300,
    isActive: false,
    repoFilters: [],
    proCursorSourceScopeMode: 'allClientSources',
    proCursorSourceIds: [],
    invalidProCursorSourceIds: [],
    createdAt: '2024-03-27T11:00:00Z',
    updatedAt: '2024-03-27T11:30:00Z'
  },
  {
    id: 'config-3',
    clientId: '1',
    organizationScopeId: 'scope-1',
    providerScopePath: 'https://dev.azure.com/meister-propr',
    providerProjectKey: 'Sandbox',
    crawlIntervalSeconds: 120,
    isActive: true,
    repoFilters: [
      {
        id: 'filter-legacy-1',
        repositoryName: 'ai-dev-days-local-test',
        displayName: 'ai-dev-days-local-test',
        canonicalSourceRef: null,
        targetBranchPatterns: ['main'],
      },
      {
        id: 'filter-legacy-2',
        repositoryName: 'meister-propr',
        displayName: 'meister-propr',
        canonicalSourceRef: null,
        targetBranchPatterns: [],
      },
    ],
    proCursorSourceScopeMode: 'allClientSources',
    proCursorSourceIds: [],
    invalidProCursorSourceIds: ['src-stale-1'],
    createdAt: '2024-01-10T09:00:00Z',
    updatedAt: '2024-01-15T14:00:00Z'
  }
]

let webhookConfigs: any[] = [
  {
    id: 'webhook-config-1',
    clientId: '1',
    provider: 'azureDevOps',
    organizationScopeId: 'scope-1',
    providerScopePath: 'https://dev.azure.com/meister-propr',
    providerProjectKey: 'Meister-ProPR',
    isActive: true,
    enabledEvents: ['pullRequestCreated', 'pullRequestUpdated', 'pullRequestCommented'],
    repoFilters: [
      {
        id: 'webhook-filter-1',
        repositoryName: 'meister-propr',
        displayName: 'meister-propr',
        canonicalSourceRef: {
          provider: 'azureDevOps',
          value: 'repo-1',
        },
        targetBranchPatterns: ['main'],
      },
    ],
    listenerUrl: 'https://propr.example.com/webhooks/v1/providers/ado/mock-path-key-1',
    createdAt: '2024-03-27T10:15:00Z',
  },
]

const webhookDeliveryLogsByConfig: Record<string, any[]> = {
  'webhook-config-1': [
    {
      id: 'webhook-log-1',
      webhookConfigurationId: 'webhook-config-1',
      receivedAt: '2024-03-27T10:20:00Z',
      eventType: 'git.pullrequest.updated',
      deliveryOutcome: 'accepted',
      httpStatusCode: 200,
      repositoryId: 'repo-1',
      pullRequestId: 42,
      sourceBranch: 'refs/heads/feature/mock',
      targetBranch: 'refs/heads/main',
      actionSummaries: ['Submitted review intake refresh'],
      failureReason: null,
      failureCategory: null,
    },
  ],
}

const adoOrganizationScopesByClient: Record<string, any[]> = {
  '1': [
    {
      id: 'scope-1',
      clientId: '1',
      organizationUrl: 'https://dev.azure.com/meister-propr',
      displayName: 'Meister Org',
      isEnabled: true,
      verificationStatus: 'verified',
      createdAt: '2024-03-20T10:00:00Z',
      updatedAt: '2024-03-27T10:00:00Z',
    },
    {
      id: 'scope-2',
      clientId: '1',
      organizationUrl: 'https://dev.azure.com/meister-propr-legacy',
      displayName: 'Legacy Sandbox',
      isEnabled: false,
      verificationStatus: 'stale',
      createdAt: '2024-03-19T10:00:00Z',
      updatedAt: '2024-03-25T10:00:00Z',
    },
  ],
  '2': [
    {
      id: 'scope-3',
      clientId: '2',
      organizationUrl: 'https://dev.azure.com/cloud-native',
      displayName: 'Cloud Native',
      isEnabled: true,
      verificationStatus: 'verified',
      createdAt: '2024-03-20T10:00:00Z',
      updatedAt: '2024-03-27T10:00:00Z',
    },
  ],
}

const adoProjectsByScope: Record<string, any[]> = {
  'scope-1': [
    { organizationScopeId: 'scope-1', projectId: 'Meister-ProPR', projectName: 'Meister-ProPR' },
    { organizationScopeId: 'scope-1', projectId: 'Sandbox', projectName: 'Sandbox' },
  ],
  'scope-3': [
    { organizationScopeId: 'scope-3', projectId: 'Infrastructure', projectName: 'Infrastructure' },
  ],
}

const adoCrawlFiltersByProject: Record<string, any[]> = {
  'scope-1::Meister-ProPR': [
    {
      canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-1' },
      displayName: 'meister-propr',
      branchSuggestions: [
        { branchName: 'main', isDefault: true },
        { branchName: 'release/*', isDefault: false },
      ],
    },
    {
      canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-2' },
      displayName: 'propr-admin-ui',
      branchSuggestions: [
        { branchName: 'main', isDefault: true },
        { branchName: 'develop', isDefault: false },
      ],
    },
  ],
  'scope-1::Sandbox': [
    {
      canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-3' },
      displayName: 'sandbox-service',
      branchSuggestions: [
        { branchName: 'main', isDefault: true },
      ],
    },
  ],
  'scope-3::Infrastructure': [
    {
      canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-4' },
      displayName: 'terraform-live',
      branchSuggestions: [
        { branchName: 'main', isDefault: true },
      ],
    },
  ],
}

const adoSourcesByProject: Record<string, any[]> = {
  'scope-1::Meister-ProPR::repository': [
    { canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-1' }, displayName: 'meister-propr' },
    { canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-2' }, displayName: 'propr-admin-ui' },
  ],
  'scope-1::Meister-ProPR::adoWiki': [
    { canonicalSourceRef: { provider: 'azureDevOps', value: 'wiki-1' }, displayName: 'Meister-ProPR.wiki' },
  ],
  'scope-1::Sandbox::repository': [
    { canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-3' }, displayName: 'sandbox-service' },
  ],
  'scope-1::Sandbox::adoWiki': [],
  'scope-3::Infrastructure::repository': [
    { canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-4' }, displayName: 'terraform-live' },
  ],
  'scope-3::Infrastructure::adoWiki': [],
}

const adoBranchesBySource: Record<string, any[]> = {
  'repo-1': [
    { branchName: 'main', isDefault: true },
    { branchName: 'release/v2', isDefault: false },
    { branchName: 'develop', isDefault: false },
  ],
  'repo-2': [
    { branchName: 'main', isDefault: true },
    { branchName: 'develop', isDefault: false },
  ],
  'repo-3': [
    { branchName: 'main', isDefault: true },
  ],
  'repo-4': [
    { branchName: 'main', isDefault: true },
    { branchName: 'staging', isDefault: false },
  ],
  'wiki-1': [
    { branchName: 'wikiMaster', isDefault: true },
  ],
}

let proCursorSourcesByClient: Record<string, any[]> = {
  '1': [
    {
      sourceId: 'src-1',
      clientId: '1',
      organizationScopeId: 'scope-1',
      providerScopePath: 'https://dev.azure.com/meister-propr',
      providerProjectKey: 'Meister-ProPR',
      repositoryId: 'repo-1',
      sourceDisplayName: 'meister-propr',
      canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-1' },
      displayName: 'Meister ProPR Docs',
      sourceKind: 'repository',
      defaultBranch: 'main',
      rootPath: '/docs',
      symbolMode: 'auto',
      isEnabled: true,
      status: 'ready',
      latestSnapshot: {
        branch: 'main',
        commitSha: 'a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2',
        freshnessStatus: 'fresh',
        supportsSymbolQueries: true,
        completedAt: new Date(Date.now() - 3600000 * 6).toISOString(),
      },
      createdAt: new Date(Date.now() - 86400000 * 14).toISOString(),
      updatedAt: new Date(Date.now() - 3600000 * 6).toISOString(),
    },
    {
      sourceId: 'src-2',
      clientId: '1',
      organizationScopeId: 'scope-1',
      providerScopePath: 'https://dev.azure.com/meister-propr',
      providerProjectKey: 'Meister-ProPR',
      repositoryId: 'wiki-1',
      sourceDisplayName: 'Meister-ProPR.wiki',
      canonicalSourceRef: { provider: 'azureDevOps', value: 'wiki-1' },
      displayName: 'Architecture Wiki',
      sourceKind: 'adoWiki',
      defaultBranch: 'wikiMaster',
      rootPath: null,
      symbolMode: 'text_only',
      isEnabled: true,
      status: 'ready',
      latestSnapshot: {
        branch: 'wikiMaster',
        commitSha: 'b2c3d4e5f6b2c3d4e5f6b2c3d4e5f6b2c3d4e5f6',
        freshnessStatus: 'stale',
        supportsSymbolQueries: false,
        completedAt: new Date(Date.now() - 86400000 * 3).toISOString(),
      },
      createdAt: new Date(Date.now() - 86400000 * 7).toISOString(),
      updatedAt: new Date(Date.now() - 86400000 * 3).toISOString(),
    },
    {
      sourceId: 'src-3',
      clientId: '1',
      organizationScopeId: 'scope-1',
      providerScopePath: 'https://dev.azure.com/meister-propr',
      providerProjectKey: 'Sandbox',
      repositoryId: 'repo-3',
      sourceDisplayName: 'sandbox-service',
      canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-3' },
      displayName: 'Sandbox Service',
      sourceKind: 'repository',
      defaultBranch: 'main',
      rootPath: null,
      symbolMode: 'auto',
      isEnabled: false,
      status: 'disabled',
      latestSnapshot: null,
      createdAt: new Date(Date.now() - 86400000 * 30).toISOString(),
      updatedAt: new Date(Date.now() - 86400000 * 10).toISOString(),
    },
  ],
  '2': [],
}

  function hoursAgoIso(hours: number) {
    return new Date(Date.now() - hours * 60 * 60 * 1000).toISOString()
  }

  function daysAgoIso(days: number, hour = 8) {
    const date = new Date()
    date.setUTCDate(date.getUTCDate() - days)
    date.setUTCHours(hour, 0, 0, 0)
    return date.toISOString()
  }

  const proCursorTopSourcesByClient: Record<string, any[]> = {
    '1': [
      {
        rank: 1,
        sourceId: 'src-1',
        sourceDisplayName: 'Meister ProPR Docs',
        totalTokens: 10420,
        estimatedCostUsd: 0.84,
        estimatedEventCount: 2,
      },
      {
        rank: 2,
        sourceId: 'src-2',
        sourceDisplayName: 'Architecture Wiki',
        totalTokens: 4820,
        estimatedCostUsd: 0.31,
        estimatedEventCount: 1,
      },
    ],
    '2': [],
  }

  const proCursorClientUsageByClient: Record<string, any> = {
    '1': {
      clientId: '1',
      from: daysAgoIso(29),
      to: daysAgoIso(0),
      granularity: 'daily',
      groupBy: 'source',
      totals: {
        promptTokens: 11640,
        completionTokens: 3600,
        totalTokens: 15240,
        estimatedCostUsd: 1.15,
        eventCount: 41,
        estimatedEventCount: 5,
      },
      includesEstimatedUsage: true,
      includesGapFilledEvents: true,
      lastRollupCompletedAtUtc: hoursAgoIso(2),
      topSources: [],
      series: [
        {
          bucketStart: daysAgoIso(5),
          promptTokens: 1320,
          completionTokens: 360,
          totalTokens: 1680,
          estimatedCostUsd: 0.13,
          breakdown: [
            { sourceId: 'src-1', sourceDisplayName: 'Meister ProPR Docs', modelName: 'text-embedding-3-large', totalTokens: 980 },
            { sourceId: 'src-2', sourceDisplayName: 'Architecture Wiki', modelName: 'gpt-4o-mini', totalTokens: 700 },
          ],
        },
        {
          bucketStart: daysAgoIso(4),
          promptTokens: 1760,
          completionTokens: 520,
          totalTokens: 2280,
          estimatedCostUsd: 0.18,
          breakdown: [
            { sourceId: 'src-1', sourceDisplayName: 'Meister ProPR Docs', modelName: 'text-embedding-3-large', totalTokens: 1460 },
            { sourceId: 'src-2', sourceDisplayName: 'Architecture Wiki', modelName: 'gpt-4o-mini', totalTokens: 820 },
          ],
        },
        {
          bucketStart: daysAgoIso(3),
          promptTokens: 2050,
          completionTokens: 710,
          totalTokens: 2760,
          estimatedCostUsd: 0.21,
          breakdown: [
            { sourceId: 'src-1', sourceDisplayName: 'Meister ProPR Docs', modelName: 'text-embedding-3-large', totalTokens: 1880 },
            { sourceId: 'src-2', sourceDisplayName: 'Architecture Wiki', modelName: 'gpt-4o-mini', totalTokens: 880 },
          ],
        },
        {
          bucketStart: daysAgoIso(2),
          promptTokens: 2410,
          completionTokens: 760,
          totalTokens: 3170,
          estimatedCostUsd: 0.24,
          breakdown: [
            { sourceId: 'src-1', sourceDisplayName: 'Meister ProPR Docs', modelName: 'text-embedding-3-large', totalTokens: 2190 },
            { sourceId: 'src-2', sourceDisplayName: 'Architecture Wiki', modelName: 'gpt-4o-mini', totalTokens: 980 },
          ],
        },
        {
          bucketStart: daysAgoIso(1),
          promptTokens: 2360,
          completionTokens: 740,
          totalTokens: 3100,
          estimatedCostUsd: 0.23,
          breakdown: [
            { sourceId: 'src-1', sourceDisplayName: 'Meister ProPR Docs', modelName: 'text-embedding-3-large', totalTokens: 2450 },
            { sourceId: 'src-2', sourceDisplayName: 'Architecture Wiki', modelName: 'gpt-4o-mini', totalTokens: 650 },
          ],
        },
        {
          bucketStart: daysAgoIso(0),
          promptTokens: 1740,
          completionTokens: 510,
          totalTokens: 2250,
          estimatedCostUsd: 0.16,
          breakdown: [
            { sourceId: 'src-1', sourceDisplayName: 'Meister ProPR Docs', modelName: 'gpt-4o-mini', totalTokens: 1460 },
            { sourceId: 'src-2', sourceDisplayName: 'Architecture Wiki', modelName: 'text-embedding-3-large', totalTokens: 790 },
          ],
        },
      ],
    },
    '2': {
      clientId: '2',
      from: daysAgoIso(29),
      to: daysAgoIso(0),
      granularity: 'daily',
      groupBy: 'source',
      totals: {
        promptTokens: 0,
        completionTokens: 0,
        totalTokens: 0,
        estimatedCostUsd: 0,
        eventCount: 0,
        estimatedEventCount: 0,
      },
      includesEstimatedUsage: false,
      includesGapFilledEvents: false,
      lastRollupCompletedAtUtc: null,
      topSources: [],
      series: [],
    },
  }

  const proCursorSourceUsageBySource: Record<string, any> = {
    'src-1': {
      sourceId: 'src-1',
      period: '30d',
      totals: {
        promptTokens: 7820,
        completionTokens: 2600,
        totalTokens: 10420,
        estimatedCostUsd: 0.84,
        eventCount: 28,
        estimatedEventCount: 2,
      },
      includesEstimatedUsage: true,
      includesGapFilledEvents: true,
      lastRollupCompletedAtUtc: hoursAgoIso(2),
      byModel: [
        { modelName: 'text-embedding-3-large', totalTokens: 6120, estimatedCostUsd: 0.34, eventCount: 17 },
        { modelName: 'gpt-4o-mini', totalTokens: 4300, estimatedCostUsd: 0.5, eventCount: 11 },
      ],
      series: [
        { bucketStart: daysAgoIso(5), promptTokens: 880, completionTokens: 280, totalTokens: 1160, estimatedCostUsd: 0.08 },
        { bucketStart: daysAgoIso(4), promptTokens: 1260, completionTokens: 390, totalTokens: 1650, estimatedCostUsd: 0.12 },
        { bucketStart: daysAgoIso(3), promptTokens: 1520, completionTokens: 520, totalTokens: 2040, estimatedCostUsd: 0.16 },
        { bucketStart: daysAgoIso(2), promptTokens: 1700, completionTokens: 610, totalTokens: 2310, estimatedCostUsd: 0.18 },
        { bucketStart: daysAgoIso(1), promptTokens: 1580, completionTokens: 510, totalTokens: 2090, estimatedCostUsd: 0.17 },
        { bucketStart: daysAgoIso(0), promptTokens: 880, completionTokens: 290, totalTokens: 1170, estimatedCostUsd: 0.13 },
      ],
    },
    'src-2': {
      sourceId: 'src-2',
      period: '30d',
      totals: {
        promptTokens: 3820,
        completionTokens: 1000,
        totalTokens: 4820,
        estimatedCostUsd: 0.31,
        eventCount: 13,
        estimatedEventCount: 1,
      },
      includesEstimatedUsage: true,
      includesGapFilledEvents: false,
      lastRollupCompletedAtUtc: hoursAgoIso(5),
      byModel: [
        { modelName: 'text-embedding-3-large', totalTokens: 2780, estimatedCostUsd: 0.14, eventCount: 8 },
        { modelName: 'gpt-4o-mini', totalTokens: 2040, estimatedCostUsd: 0.17, eventCount: 5 },
      ],
      series: [
        { bucketStart: daysAgoIso(5), promptTokens: 440, completionTokens: 140, totalTokens: 580, estimatedCostUsd: 0.04 },
        { bucketStart: daysAgoIso(4), promptTokens: 500, completionTokens: 170, totalTokens: 670, estimatedCostUsd: 0.05 },
        { bucketStart: daysAgoIso(3), promptTokens: 530, completionTokens: 190, totalTokens: 720, estimatedCostUsd: 0.05 },
        { bucketStart: daysAgoIso(2), promptTokens: 710, completionTokens: 150, totalTokens: 860, estimatedCostUsd: 0.06 },
        { bucketStart: daysAgoIso(1), promptTokens: 840, completionTokens: 110, totalTokens: 950, estimatedCostUsd: 0.06 },
        { bucketStart: daysAgoIso(0), promptTokens: 800, completionTokens: 240, totalTokens: 1040, estimatedCostUsd: 0.05 },
      ],
    },
    'src-3': {
      sourceId: 'src-3',
      period: '30d',
      totals: {
        promptTokens: 0,
        completionTokens: 0,
        totalTokens: 0,
        estimatedCostUsd: 0,
        eventCount: 0,
        estimatedEventCount: 0,
      },
      includesEstimatedUsage: false,
      includesGapFilledEvents: false,
      lastRollupCompletedAtUtc: null,
      byModel: [],
      series: [],
    },
  }

  const proCursorRecentEventsBySource: Record<string, any[]> = {
    'src-1': [
      {
        occurredAtUtc: hoursAgoIso(1),
        callType: 'semantic_search',
        modelName: 'gpt-4o-mini',
        deploymentName: 'knowledge-reasoning',
        totalTokens: 420,
        promptTokens: 320,
        completionTokens: 100,
        estimatedCostUsd: 0.05,
        sourcePath: '/docs/architecture/review-memory.md',
        resourceId: 'docs-review-memory',
        requestId: 'req-pc-1a2b3c4d5e6f',
        tokensEstimated: false,
        costEstimated: false,
      },
      {
        occurredAtUtc: hoursAgoIso(3),
        callType: 'embedding_index',
        modelName: 'text-embedding-3-large',
        deploymentName: 'knowledge-embeddings',
        totalTokens: 1180,
        promptTokens: 1180,
        completionTokens: 0,
        estimatedCostUsd: 0.06,
        sourcePath: '/docs/admin/token-governance.md',
        resourceId: 'docs-token-governance',
        requestId: 'req-pc-7f8e9d0c1b2a',
        tokensEstimated: false,
        costEstimated: false,
      },
      {
        occurredAtUtc: hoursAgoIso(6),
        callType: 'symbol_lookup',
        modelName: 'gpt-4o-mini',
        deploymentName: 'knowledge-reasoning',
        totalTokens: 290,
        promptTokens: 210,
        completionTokens: 80,
        estimatedCostUsd: 0.03,
        sourcePath: '/docs/runtime/procursor-sources.md',
        resourceId: 'docs-procursor-sources',
        requestId: 'req-pc-9a8b7c6d5e4f',
        tokensEstimated: true,
        costEstimated: true,
      },
      {
        occurredAtUtc: hoursAgoIso(10),
        callType: 'semantic_search',
        modelName: 'gpt-4o-mini',
        deploymentName: 'knowledge-reasoning',
        totalTokens: 360,
        promptTokens: 260,
        completionTokens: 100,
        estimatedCostUsd: 0.04,
        sourcePath: '/docs/runbooks/source-refresh.md',
        resourceId: 'docs-source-refresh',
        requestId: 'req-pc-112233445566',
        tokensEstimated: false,
        costEstimated: false,
      },
      {
        occurredAtUtc: hoursAgoIso(15),
        callType: 'embedding_index',
        modelName: 'text-embedding-3-large',
        deploymentName: 'knowledge-embeddings',
        totalTokens: 940,
        promptTokens: 940,
        completionTokens: 0,
        estimatedCostUsd: 0.04,
        sourcePath: '/docs/security/secret-storage.md',
        resourceId: 'docs-secret-storage',
        requestId: 'req-pc-abcdef123456',
        tokensEstimated: false,
        costEstimated: false,
      },
    ],
    'src-2': [
      {
        occurredAtUtc: hoursAgoIso(8),
        callType: 'semantic_search',
        modelName: 'gpt-4o-mini',
        deploymentName: 'knowledge-reasoning',
        totalTokens: 240,
        promptTokens: 180,
        completionTokens: 60,
        estimatedCostUsd: 0.02,
        sourcePath: '/wiki/architecture/review-pipeline',
        resourceId: 'wiki-review-pipeline',
        requestId: 'req-pc-fedcba654321',
        tokensEstimated: false,
        costEstimated: false,
      },
      {
        occurredAtUtc: hoursAgoIso(20),
        callType: 'embedding_index',
        modelName: 'text-embedding-3-large',
        deploymentName: 'knowledge-embeddings',
        totalTokens: 720,
        promptTokens: 720,
        completionTokens: 0,
        estimatedCostUsd: 0.03,
        sourcePath: '/wiki/admin/protocol-audit',
        resourceId: 'wiki-protocol-audit',
        requestId: 'req-pc-334455667788',
        tokensEstimated: true,
        costEstimated: true,
      },
    ],
    'src-3': [],
  }

function getScope(clientId: string, scopeId: string | null | undefined) {
  if (!scopeId) {
    return null
  }

  return (adoOrganizationScopesByClient[clientId] ?? []).find((scope) => scope.id === scopeId) ?? null
}

function getCrawlFilters(scopeId: string | null | undefined, projectId: string | null | undefined) {
  if (!scopeId || !projectId) {
    return []
  }

  return adoCrawlFiltersByProject[`${scopeId}::${projectId}`] ?? []
}

function getProviderConnection(clientId: string, connectionId: string | null | undefined) {
  if (!connectionId) {
    return null
  }

  return (providerConnectionsByClient[clientId] ?? [])
    .filter((connection) => isProviderEnabled(connection.providerFamily))
    .find((connection) => connection.id === connectionId) ?? null
}

function isProviderEnabled(providerFamily: string | null | undefined) {
  return providerActivationStatuses.find((status) => status.providerFamily === providerFamily)?.isEnabled ?? true
}

function buildProviderAuditTrail(clientId: string) {
  return (providerConnectionsByClient[clientId] ?? [])
    .filter((connection) => isProviderEnabled(connection.providerFamily))
    .flatMap((connection) => {
      const entries = [
        {
          id: `${connection.id}:created`,
          clientId,
          connectionId: connection.id,
          providerFamily: connection.providerFamily,
          displayName: connection.displayName,
          hostBaseUrl: connection.hostBaseUrl,
          eventType: 'connectionCreated',
          summary: `Connection created for ${connection.displayName}.`,
          occurredAt: connection.createdAt,
          status: 'info',
          failureCategory: null,
          detail: null,
        },
      ]

      if (connection.updatedAt && connection.updatedAt !== connection.createdAt) {
        entries.push({
          id: `${connection.id}:updated`,
          clientId,
          connectionId: connection.id,
          providerFamily: connection.providerFamily,
          displayName: connection.displayName,
          hostBaseUrl: connection.hostBaseUrl,
          eventType: connection.isActive ? 'connectionUpdated' : 'connectionDisabled',
          summary: connection.isActive
            ? `Connection updated for ${connection.displayName}.`
            : `Connection disabled for ${connection.displayName}.`,
          occurredAt: connection.updatedAt,
          status: connection.isActive ? 'info' : 'warning',
          failureCategory: null,
          detail: null,
        })
      }

      if (connection.lastVerifiedAt) {
        const isFailed = connection.verificationStatus?.toLowerCase() === 'failed'
        entries.push({
          id: `${connection.id}:verified`,
          clientId,
          connectionId: connection.id,
          providerFamily: connection.providerFamily,
          displayName: connection.displayName,
          hostBaseUrl: connection.hostBaseUrl,
          eventType: isFailed ? 'connectionVerificationFailed' : 'connectionVerified',
          summary: isFailed
            ? `Verification failed for ${connection.displayName}.`
            : `Connection verified for ${connection.displayName}.`,
          occurredAt: connection.lastVerifiedAt,
          status: isFailed ? 'error' : 'success',
          failureCategory: connection.lastVerificationFailureCategory ?? null,
          detail: connection.lastVerificationError ?? null,
        })
      }

      return entries
    })
    .sort((left, right) => Date.parse(right.occurredAt) - Date.parse(left.occurredAt))
}

let dismissedFindings = [
  {
    id: 'd1',
    clientId: '1',
    patternText: 'postgres uses hardcoded credentials postgrespassword devpass ensure this compose file is strictly for development/test',
    label: 'False positive: dev credentials',
    createdAt: new Date(Date.now() - 86400000).toISOString()
  },
  {
    id: 'd2',
    clientId: '1',
    patternText: 'Potential use of insecure industrial protocol (Modbus/TCP) without TLS encryption layer in the communication stack.',
    label: 'Intentional: Legacy support',
    createdAt: new Date(Date.now() - 172800000).toISOString()
  }
]

let promptOverrides = [
  {
    id: 'o1',
    clientId: '1',
    scope: 'clientScope',
    promptKey: 'SystemPrompt',
    overrideText: 'You are an expert code reviewer specialising in .NET/C# and general cloud-native architecture. Prioritize security and naming consistency.',
    createdAt: new Date(Date.now() - 86400000 * 2).toISOString(),
    updatedAt: new Date(Date.now() - 86400000 * 2).toISOString()
  },
  {
    id: 'o2',
    clientId: '1',
    scope: 'clientScope',
    promptKey: 'AgenticLoopGuidance',
    overrideText: 'When reviewing Bicep files, always check for resource naming best practices and ensure identity-based access is used over connection strings.',
    createdAt: new Date(Date.now() - 86400000 * 3).toISOString(),
    updatedAt: new Date(Date.now() - 86400000 * 3).toISOString()
  }
]

// The wire shapes each driver speaks, as the server reports them. Kept in one place so the mock cannot drift
// into offering a shape no driver can serve — which is the whole point of the endpoint that returns it.
const mockDriverProtocolModes: Array<[string, string[]]> = [
  ['azureOpenAi', ['auto', 'responses', 'chatCompletions', 'embeddings']],
  ['openAi', ['auto', 'responses', 'chatCompletions', 'embeddings']],
  ['liteLlm', ['auto', 'responses', 'chatCompletions', 'embeddings']],
  ['openAiCompatible', ['auto', 'chatCompletions', 'embeddings']],
  ['anthropic', ['auto', 'anthropicMessages']],
  ['awsBedrock', ['auto', 'bedrockConverse', 'embeddings']],
  ['googleVertex', ['auto', 'googleGenerateContent', 'embeddings']],
]

function mockDiscoveredModel(remoteModelId: string, displayName: string, protocolMode: string, embedding = false) {
  return {
    id: `discovered-${remoteModelId}`,
    remoteModelId,
    displayName,
    operationKinds: [embedding ? 'embedding' : 'chat'],
    supportedProtocolModes: ['auto', protocolMode],
    supportsChat: !embedding,
    supportsEmbedding: embedding,
    supportsToolUse: !embedding,
    supportsStructuredOutput: !embedding,
    source: 'discovered',
    lastSeenAt: new Date().toISOString(),
  }
}

// What each provider's discovery returns. The native families deliberately return their own id shapes —
// a Bedrock id is not a vendor id, and that is what an operator has to recognise in the picker.
const mockDiscoveredModels: Record<string, any[]> = {
  azureOpenAi: [
    mockDiscoveredModel('gpt-4o', 'GPT-4o', 'responses'),
    mockDiscoveredModel('text-embedding-3-large', 'text-embedding-3-large', 'embeddings', true),
  ],
  openAi: [mockDiscoveredModel('gpt-4o', 'GPT-4o', 'responses')],
  liteLlm: [mockDiscoveredModel('claude-opus-4-5', 'claude-opus-4-5', 'chatCompletions')],
  openAiCompatible: [
    mockDiscoveredModel('deepseek-v4-flash', 'deepseek-v4-flash', 'chatCompletions'),
    mockDiscoveredModel('kimi-k2.7-code', 'kimi-k2.7-code', 'chatCompletions'),
  ],
  anthropic: [
    mockDiscoveredModel('claude-opus-4-5', 'claude-opus-4-5', 'anthropicMessages'),
    mockDiscoveredModel('claude-sonnet-4-5', 'claude-sonnet-4-5', 'anthropicMessages'),
  ],
  awsBedrock: [
    mockDiscoveredModel('anthropic.claude-opus-4-5', 'Anthropic Claude Opus 4.5', 'bedrockConverse'),
    mockDiscoveredModel('amazon.titan-embed-text-v2:0', 'Amazon Titan Text Embeddings V2', 'embeddings', true),
  ],
  googleVertex: [
    mockDiscoveredModel('gemini-3-pro', 'Gemini 3 Pro', 'googleGenerateContent'),
    mockDiscoveredModel('text-embedding-005', 'Text Embedding 005', 'embeddings', true),
  ],
}

// The notices a driver attaches to discovery, which are load-bearing for the two cloud families: without them an
// operator hits an inference-profile rejection, or waits for a model list Vertex never publishes.
const mockDiscoveryWarnings: Record<string, string[]> = {
  awsBedrock: [
    'Some Bedrock models can only be called through an inference profile. Where the account requires one, use the profile ID as the model ID.',
  ],
  googleVertex: [
    "Vertex AI does not list its models on this endpoint; enter the model IDs to use, for example 'gemini-3-pro'.",
  ],
}

// The catalog the picker browses. Prices are per million tokens, as the contract has them.
const mockCatalogProviders = [
  { providerId: 'anthropic', providerName: 'Anthropic', modelCount: 2 },
  { providerId: 'amazon-bedrock', providerName: 'Amazon Bedrock', modelCount: 1 },
  { providerId: 'google-vertex', providerName: 'Google Vertex AI', modelCount: 1 },
  { providerId: 'opencode', providerName: 'opencode Zen', modelCount: 1 },
]

function mockCatalogEntry(
  providerId: string,
  providerName: string,
  remoteModelId: string,
  displayName: string,
  inputCost: number,
  outputCost: number,
  extras: Record<string, unknown> = {},
) {
  return {
    providerId,
    providerName,
    remoteModelId,
    displayName,
    family: providerId,
    supportsToolUse: true,
    supportsStructuredOutput: true,
    supportsReasoning: true,
    supportsPromptCaching: true,
    maxContextTokens: 200000,
    maxOutputTokens: 64000,
    inputCostPer1MUsd: inputCost,
    outputCostPer1MUsd: outputCost,
    cachedInputCostPer1MUsd: inputCost / 10,
    cacheWriteCostPer1MUsd: inputCost * 1.25,
    openWeights: false,
    releaseDate: '2026-05-01',
    pricingLayer: 'global',
    ...extras,
  }
}

// A tenant's own price overrides, mutable so saving one and reloading shows it.
let mockCatalogOverrides: any[] = []

const mockCatalogModels = [
  mockCatalogEntry('anthropic', 'Anthropic', 'claude-opus-4-5', 'Claude Opus 4.5', 5, 25),
  mockCatalogEntry('anthropic', 'Anthropic', 'claude-sonnet-4-5', 'Claude Sonnet 4.5', 3, 15),
  mockCatalogEntry('amazon-bedrock', 'Amazon Bedrock', 'anthropic.claude-opus-4-5', 'Claude Opus 4.5 (Bedrock)', 5, 25),
  mockCatalogEntry('google-vertex', 'Google Vertex AI', 'gemini-3-pro', 'Gemini 3 Pro', 2, 12),
  // A negotiated rate, so the picker's pricing-layer label has something to report.
  mockCatalogEntry('opencode', 'opencode Zen', 'deepseek-v4-flash', 'DeepSeek V4 Flash', 1.74, 3.84, { pricingLayer: 'tenant' }),
]

function filterCatalogModels(request: Request) {
  const providerId = new URL(request.url).searchParams.get('providerId')
  return providerId ? mockCatalogModels.filter((model) => model.providerId === providerId) : mockCatalogModels
}

// Profiles as the API returns them: a provider family, a base URL and an auth mode. Fixtures written before
// those fields existed rendered as "Unknown / Unavailable", which said nothing true about the profile.
// Several families are represented on purpose — mixing them is the point of provider breadth, and the mock is
// where that is visible without an account for each one.
let aiConnectionsByClient: Record<string, any[]> = {
  '1': [
    {
      id: 'ai-1',
      clientId: '1',
      displayName: 'Azure OpenAI Prod',
      providerKind: 'azureOpenAi',
      baseUrl: 'https://acme-prod.openai.azure.com/',
      authMode: 'apiKey',
      discoveryMode: 'providerCatalog',
      isActive: true,
      configuredModels: [
        { id: 'm-gpt4o', displayName: 'GPT-4o', remoteModelId: 'gpt-4o', supportsChat: true, supportsEmbedding: false, supportedProtocolModes: ['auto', 'responses'] },
        { id: 'm-gpt4o-mini', displayName: 'GPT-4o mini', remoteModelId: 'gpt-4o-mini', supportsChat: true, supportsEmbedding: false, supportedProtocolModes: ['auto', 'responses'] },
        { id: 'm-embed3', displayName: 'text-embedding-3-large', remoteModelId: 'text-embedding-3-large', supportsChat: false, supportsEmbedding: true, supportedProtocolModes: ['auto', 'embeddings'], tokenizerName: 'cl100k_base', maxInputTokens: 8192, embeddingDimensions: 3072 },
      ],
      purposeBindings: [],
      verification: { status: 'verified', summary: 'Verified connectivity for the Azure AI resource.' },
      createdAt: new Date(Date.now() - 86400000 * 7).toISOString(),
      updatedAt: new Date(Date.now() - 3600000).toISOString(),
    },
    {
      id: 'ai-2',
      clientId: '1',
      displayName: 'Claude (native)',
      providerKind: 'anthropic',
      baseUrl: 'https://api.anthropic.com/v1',
      authMode: 'apiKey',
      discoveryMode: 'providerCatalog',
      isActive: true,
      configuredModels: [
        { id: 'm-opus', displayName: 'claude-opus-4-5', remoteModelId: 'claude-opus-4-5', supportsChat: true, supportsEmbedding: false, supportedProtocolModes: ['auto', 'anthropicMessages'], supportsPromptCaching: true, supportsReasoning: true, inputCostPer1MUsd: 5, outputCostPer1MUsd: 25 },
      ],
      purposeBindings: [],
      verification: { status: 'verified', summary: "Verified Anthropic connectivity for 'https://api.anthropic.com/v1'." },
      createdAt: new Date(Date.now() - 86400000 * 2).toISOString(),
      updatedAt: new Date(Date.now() - 1800000).toISOString(),
    },
    {
      id: 'ai-3',
      clientId: '1',
      displayName: 'Bedrock (eu-central-1)',
      providerKind: 'awsBedrock',
      baseUrl: 'https://bedrock-runtime.eu-central-1.amazonaws.com',
      authMode: 'apiKey',
      discoveryMode: 'providerCatalog',
      isActive: false,
      configuredModels: [
        { id: 'm-bedrock-opus', displayName: 'Anthropic Claude Opus 4.5', remoteModelId: 'anthropic.claude-opus-4-5', supportsChat: true, supportsEmbedding: false, supportedProtocolModes: ['auto', 'bedrockConverse'] },
        { id: 'm-titan-embed', displayName: 'Amazon Titan Text Embeddings V2', remoteModelId: 'amazon.titan-embed-text-v2:0', supportsChat: false, supportsEmbedding: true, supportedProtocolModes: ['auto', 'embeddings'], tokenizerName: 'cl100k_base', maxInputTokens: 8192, embeddingDimensions: 1024 },
      ],
      purposeBindings: [],
      verification: { status: 'verified', summary: "Verified AWS Bedrock access in 'eu-central-1' (2 models)." },
      createdAt: new Date(Date.now() - 86400000 * 4).toISOString(),
      updatedAt: new Date(Date.now() - 86400000).toISOString(),
    },
  ],
}

let providerActivationStatuses = [
  {
    providerFamily: 'azureDevOps',
    isEnabled: true,
    baselineAdapterSetRegistered: true,
    registeredCapabilities: ['repositoryDiscovery', 'activePullRequestDiscovery', 'reviewThreadReply'],
    supportClaimReadiness: 'workflowComplete',
    supportClaimReason: 'Azure DevOps is fully supported.',
    updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
  },
  {
    providerFamily: 'github',
    isEnabled: false,
    baselineAdapterSetRegistered: true,
    registeredCapabilities: ['repositoryDiscovery', 'activePullRequestDiscovery', 'reviewThreadReply'],
    supportClaimReadiness: 'onboardingReady',
    supportClaimReason: 'GitHub remains onboarding ready when enabled.',
    updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
  },
  {
    providerFamily: 'gitLab',
    isEnabled: true,
    baselineAdapterSetRegistered: true,
    registeredCapabilities: ['repositoryDiscovery', 'activePullRequestDiscovery', 'reviewThreadReply'],
    supportClaimReadiness: 'onboardingReady',
    supportClaimReason: 'GitLab remains onboarding ready when enabled.',
    updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
  },
  {
    providerFamily: 'forgejo',
    isEnabled: false,
    baselineAdapterSetRegistered: true,
    registeredCapabilities: ['repositoryDiscovery', 'activePullRequestDiscovery', 'reviewThreadReply'],
    supportClaimReadiness: 'onboardingReady',
    supportClaimReason: 'Forgejo remains onboarding ready when enabled.',
    updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
  },
]

let providerConnectionsByClient: Record<string, any[]> = {
  '1': [
    {
      id: 'provider-conn-ado-1',
      clientId: '1',
      providerFamily: 'azureDevOps',
      hostBaseUrl: 'https://dev.azure.com',
      authenticationKind: 'oauthClientCredentials',
      gitHubAppId: null,
      gitHubAppInstallationId: null,
      displayName: 'Meister Azure DevOps',
      isActive: true,
      verificationStatus: 'verified',
      lastVerifiedAt: new Date(Date.now() - 4 * 3600000).toISOString(),
      lastVerificationError: null,
      lastVerificationFailureCategory: null,
      createdAt: new Date(Date.now() - 86400000 * 7).toISOString(),
      updatedAt: new Date(Date.now() - 4 * 3600000).toISOString(),
    },
    {
      id: 'provider-conn-github-1',
      clientId: '1',
      providerFamily: 'github',
      hostBaseUrl: 'https://github.com',
      authenticationKind: 'personalAccessToken',
      gitHubAppId: null,
      gitHubAppInstallationId: null,
      displayName: 'Acme GitHub',
      isActive: true,
      verificationStatus: 'verified',
      lastVerifiedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
      lastVerificationError: null,
      lastVerificationFailureCategory: null,
      createdAt: new Date(Date.now() - 86400000 * 3).toISOString(),
      updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
    },
  ],
  '2': [
    {
      id: 'provider-conn-gitlab-1',
      clientId: '2',
      providerFamily: 'gitLab',
      hostBaseUrl: 'https://gitlab.example.com',
      authenticationKind: 'personalAccessToken',
      gitHubAppId: null,
      gitHubAppInstallationId: null,
      displayName: 'Platform GitLab',
      isActive: false,
      verificationStatus: 'stale',
      lastVerifiedAt: new Date(Date.now() - 36 * 3600000).toISOString(),
      lastVerificationError: 'Token missing read_api scope.',
      lastVerificationFailureCategory: 'authentication',
      createdAt: new Date(Date.now() - 86400000 * 10).toISOString(),
      updatedAt: new Date(Date.now() - 36 * 3600000).toISOString(),
    },
  ],
}

let providerScopesByConnection: Record<string, any[]> = {
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
      lastVerifiedAt: new Date(Date.now() - 4 * 3600000).toISOString(),
      lastVerificationError: null,
      createdAt: new Date(Date.now() - 86400000 * 7).toISOString(),
      updatedAt: new Date(Date.now() - 4 * 3600000).toISOString(),
    },
    {
      // Matches the provider scope path carried on the review-history PR links so the
      // retained-archive section can resolve this connection from the PR view route.
      id: 'provider-scope-ado-acme',
      clientId: '1',
      connectionId: 'provider-conn-ado-1',
      scopeType: 'organization',
      externalScopeId: 'acme',
      scopePath: 'https://dev.azure.com/acme',
      displayName: 'Acme Org',
      verificationStatus: 'verified',
      isEnabled: true,
      lastVerifiedAt: new Date(Date.now() - 4 * 3600000).toISOString(),
      lastVerificationError: null,
      createdAt: new Date(Date.now() - 86400000 * 7).toISOString(),
      updatedAt: new Date(Date.now() - 4 * 3600000).toISOString(),
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
      lastVerifiedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
      lastVerificationError: null,
      createdAt: new Date(Date.now() - 86400000 * 3).toISOString(),
      updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
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
      lastVerifiedAt: new Date(Date.now() - 36 * 3600000).toISOString(),
      lastVerificationError: 'The scope is no longer reachable with the stored token.',
      createdAt: new Date(Date.now() - 86400000 * 10).toISOString(),
      updatedAt: new Date(Date.now() - 36 * 3600000).toISOString(),
    },
  ],
}

// Retained pull request archive data, served for the repository + pull request shown by the
// review-history links (repository `backend-service`, pull request #42). The owning connection is
// resolved server-side, so the read endpoints carry no connectionId. Any other repository/PR
// combination yields no retained data so the section's empty state is exercised.
const retainedArchiveRepositoryId = 'backend-service'
const retainedArchivePullRequestId = 42

function hasRetainedArchive(repositoryId: string | null, pullRequestId: number): boolean {
  return repositoryId === retainedArchiveRepositoryId && pullRequestId === retainedArchivePullRequestId
}

const retainedThreads = [
  {
    threadId: 'thread-pr-1',
    filePath: null,
    line: null,
    status: 'Active',
    updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
    comments: [
      {
        commentId: 'comment-pr-1-human',
        authorIdentity: 'jane.developer',
        isAiAuthored: false,
        publishedAt: new Date(Date.now() - 3 * 3600000).toISOString(),
        body: 'Can we double-check the token refresh path before merging?',
      },
    ],
  },
  {
    threadId: 'thread-file-1',
    filePath: 'src/auth/middleware.ts',
    line: 42,
    status: 'Fixed',
    updatedAt: new Date(Date.now() - 90 * 60000).toISOString(),
    comments: [
      {
        commentId: 'comment-file-1-ai',
        authorIdentity: 'ProPR Reviewer',
        isAiAuthored: true,
        publishedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
        body: 'This handler swallows the rejected token error; surface it so callers can react.',
        // Provenance: the review run that produced this AI comment, surfaced as a "View trace" link.
        originatingJobId: '11111111-2222-3333-4444-555555555555',
      },
      {
        commentId: 'comment-file-1-human',
        authorIdentity: 'jane.developer',
        isAiAuthored: false,
        publishedAt: new Date(Date.now() - 100 * 60000).toISOString(),
        body: 'Good catch, fixed by rethrowing with context.',
      },
    ],
  },
]

const retainedFiles = [
  {
    filePath: 'src/auth/middleware.ts',
    revisionKey: 'rev-2',
    changeType: 'modified',
    isBinary: false,
    createdAt: new Date(Date.now() - 90 * 60000).toISOString(),
  },
  {
    filePath: 'src/auth/tokens.ts',
    revisionKey: 'rev-2',
    changeType: 'added',
    isBinary: false,
    createdAt: new Date(Date.now() - 90 * 60000).toISOString(),
  },
]

const retainedFileDiffs = [
  {
    filePath: 'src/auth/middleware.ts',
    revisionKey: 'rev-2',
    changeType: 'modified',
    isBinary: false,
    createdAt: new Date(Date.now() - 90 * 60000).toISOString(),
    unifiedDiff: [
      'diff --git a/src/auth/middleware.ts b/src/auth/middleware.ts',
      'index 1111111..2222222 100644',
      '--- a/src/auth/middleware.ts',
      '+++ b/src/auth/middleware.ts',
      '@@ -39,7 +39,9 @@ export function authenticate(req: Request): Principal {',
      '   const token = readBearerToken(req)',
      '   if (!token) {',
      '-    return anonymous()',
      '+    throw new UnauthorizedError(\'missing bearer token\')',
      '   }',
      '-  return verify(token)',
      '+  const principal = verify(token)',
      '+  return principal',
      ' }',
    ].join('\n'),
  },
  {
    filePath: 'src/auth/tokens.ts',
    revisionKey: 'rev-2',
    changeType: 'added',
    isBinary: false,
    createdAt: new Date(Date.now() - 90 * 60000).toISOString(),
    unifiedDiff: [
      'diff --git a/src/auth/tokens.ts b/src/auth/tokens.ts',
      'new file mode 100644',
      'index 0000000..3333333',
      '--- /dev/null',
      '+++ b/src/auth/tokens.ts',
      '@@ -0,0 +1,3 @@',
      '+export function verify(token: string): Principal {',
      '+  return decode(token)',
      '+}',
    ].join('\n'),
  },
]

let providerReviewerIdentitiesByConnection: Record<string, any | null> = {
  'provider-conn-ado-1': {
    id: 'provider-reviewer-ado-1',
    clientId: '1',
    connectionId: 'provider-conn-ado-1',
    providerFamily: 'azureDevOps',
    externalUserId: 'ado-reviewer-1',
    login: 'meister-bot',
    displayName: 'Meister Bot',
    isBot: true,
    updatedAt: new Date(Date.now() - 4 * 3600000).toISOString(),
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
    updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
  },
  'provider-conn-gitlab-1': null,
}

const providerReviewerIdentityCandidatesByConnection: Record<string, any[]> = {
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
      updatedAt: new Date(Date.now() - 4 * 3600000).toISOString(),
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
      updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
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
      updatedAt: new Date(Date.now() - 6 * 3600000).toISOString(),
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
      updatedAt: new Date(Date.now() - 36 * 3600000).toISOString(),
    },
  ],
}

let threadMemoryRecords = [
  {
    id: 'tm-1',
    clientId: '1',
    threadId: '1024',
    repositoryId: 'meister-propr',
    pullRequestId: 450,
    filePath: 'src/MeisterDev.ProPR.Api/Features/Reviewing/Diagnostics/Controllers/JobsController.cs',
    resolutionSummary: 'The user requested to add a new endpoint for fetching job protocols. The developer implemented it by adding the `GetProtocol` method to the `JobsController`.',
    createdAt: new Date(Date.now() - 86400000 * 5).toISOString(),
    updatedAt: new Date(Date.now() - 86400000 * 5).toISOString()
  },
  {
    id: 'tm-2',
    clientId: '1',
    threadId: '1025',
    repositoryId: 'meister-propr',
    pullRequestId: 450,
    filePath: 'src/MeisterDev.ProPR.Core/Services/JobService.cs',
    resolutionSummary: 'Fixed a race condition in the job status update logic by implementing a distributed lock using Redis.',
    createdAt: new Date(Date.now() - 86400000 * 4).toISOString(),
    updatedAt: new Date(Date.now() - 86400000 * 4).toISOString()
  },
  {
    id: 'tm-3',
    clientId: '2',
    threadId: '501',
    repositoryId: 'infrastructure',
    pullRequestId: 12,
    filePath: 'terraform/main.tf',
    resolutionSummary: 'Updated the CIDR block for the production VNET to avoid overlap with the management network.',
    createdAt: new Date(Date.now() - 86400000 * 10).toISOString(),
    updatedAt: new Date(Date.now() - 86400000 * 10).toISOString()
  }
]

let memoryActivityLog = [
  {
    id: 'log-1',
    clientId: '1',
    threadId: '1024',
    repositoryId: 'meister-propr',
    pullRequestId: 450,
    action: 0,
    previousStatus: null,
    currentStatus: 'resolved',
    reason: 'Thread resolution summary generated and stored.',
    occurredAt: new Date(Date.now() - 86400000 * 5).toISOString()
  },
  {
    id: 'log-2',
    clientId: '1',
    threadId: '1025',
    repositoryId: 'meister-propr',
    pullRequestId: 450,
    action: 0,
    previousStatus: 'active',
    currentStatus: 'resolved',
    reason: 'Thread resolved by developer, summary updated.',
    occurredAt: new Date(Date.now() - 86400000 * 4).toISOString()
  },
  {
    id: 'log-3',
    clientId: '1',
    threadId: '1026',
    repositoryId: 'meister-propr',
    pullRequestId: 451,
    action: 2,
    previousStatus: 'active',
    currentStatus: 'active',
    reason: 'Thread still active, no summary generated.',
    occurredAt: new Date(Date.now() - 86400000 * 2).toISOString()
  }
]

// Blocked pull requests, keyed by clientId. Populated/cleared via the blocked-prs handlers below.
const mockBlockedPrsByClient: Record<string, any[]> = {}

// ---- Logical models ----
// These stores are id-agnostic: the handlers return the same representative dataset regardless of which
// client/tenant the UI navigates to, so the editors always have data to render in the mock UI.

// The connections a tenant admin may reference (across the tenant's clients), each with its models' capabilities.
// Tenant-scoped connections (AiConnectionDto shape) — defined at the tenant, inherited by its clients.
let mockTenantConnections: any[] = [
  {
    id: 'tc-azure',
    tenantId: 'tenant-1',
    clientId: null,
    displayName: 'Tenant Azure OpenAI',
    providerKind: 'azureOpenAi',
    baseUrl: 'https://tenant-shared.openai.azure.com/',
    authMode: 'apiKey',
    discoveryMode: 'manualOnly',
    isActive: false,
    configuredModels: [
      { id: 'tc-gpt4o', displayName: 'GPT-4o', remoteModelId: 'gpt-4o', supportsChat: true, supportsEmbedding: false },
      { id: 'tc-gpt4o-mini', displayName: 'GPT-4o mini', remoteModelId: 'gpt-4o-mini', supportsChat: true, supportsEmbedding: false },
      { id: 'tc-embed3', displayName: 'text-embedding-3-large', remoteModelId: 'text-embedding-3-large', supportsChat: false, supportsEmbedding: true },
    ],
    purposeBindings: [],
    verification: { status: 'verified', summary: 'Verified against the provider catalog.' },
    createdAt: new Date(Date.now() - 86400000 * 3).toISOString(),
    updatedAt: new Date(Date.now() - 3600000).toISOString(),
  },
]

// The tenant-catalog logical models a tenant's clients inherit — each points at a tenant connection above.
let mockTenantLogicalModels = [
  { id: 'lm-deep', name: 'deep-review', capability: 'chat', connectionId: 'tc-azure', configuredModelId: 'tc-gpt4o', reasoningEffort: 'high', protocolMode: 'auto', scope: 'tenant' },
  { id: 'lm-fast', name: 'fast-triage', capability: 'chat', connectionId: 'tc-azure', configuredModelId: 'tc-gpt4o-mini', reasoningEffort: 'low', protocolMode: 'auto', scope: 'tenant' },
  { id: 'lm-embed', name: 'embed-default', capability: 'embedding', connectionId: 'tc-azure', configuredModelId: 'tc-embed3', reasoningEffort: 'none', protocolMode: 'embeddings', scope: 'tenant' },
]

// A per-client override that shadows the tenant "deep-review" with a different reasoning effort.
let mockClientLogicalOverrides = [
  { id: 'ov-deep', name: 'deep-review', capability: 'chat', connectionId: 'ai-1', configuredModelId: 'm-gpt4o', reasoningEffort: 'medium', protocolMode: 'auto', scope: 'client' },
]

// The client's purpose → logical-model map.
let mockClientPurposeRoles: Record<string, string> = {
  reviewTriage: 'fast-triage',
  embeddingDefault: 'embed-default',
}

// The client's effective logical models: its overrides plus the tenant-catalog entries an override does not shadow.
function effectiveLogicalModels(): unknown[] {
  const overrideNames = new Set(mockClientLogicalOverrides.map((entry) => entry.name))
  return [
    ...mockClientLogicalOverrides,
    ...mockTenantLogicalModels.filter((entry) => !overrideNames.has(entry.name)),
  ]
}

function logicalModelFromBody(body: any, scope: string): any {
  return {
    id: `lm-${Math.random().toString(36).slice(2, 10)}`,
    name: body.name,
    capability: body.capability ?? 'chat',
    connectionId: body.connectionId ?? '',
    configuredModelId: body.configuredModelId ?? '',
    reasoningEffort: body.reasoningEffort ?? 'none',
    protocolMode: body.protocolMode ?? 'auto',
    scope,
  }
}

// The mapping fields an update PUT changes on an existing entry (its id/name/scope are preserved).
function updatedMapping(entry: any, body: any): any {
  return {
    capability: body.capability ?? entry.capability,
    connectionId: body.connectionId ?? entry.connectionId,
    configuredModelId: body.configuredModelId ?? entry.configuredModelId,
    reasoningEffort: body.reasoningEffort ?? entry.reasoningEffort,
    protocolMode: body.protocolMode ?? entry.protocolMode,
  }
}


/** A metric payload with every field present, so a view never has to defend against a partial mock. */
function mockMetric(overrides: Record<string, unknown> = {}) {
  return {
    precision: null,
    recall: null,
    f1: null,
    acceptanceRate: null,
    addressed: 18,
    acknowledged: 4,
    dismissed: 3,
    falsePositive: 6,
    misses: 9,
    sampleSize: 12,
    discussed: 0,
    ...overrides,
  }
}

/** Findings for the drill-through, including one still open so both outcome states are visible. */
function mockCodeInsightFindings(coreType: string | null) {
  const all = [
    {
      id: 'finding-1',
      clientId: '1',
      repositoryId: 'payments-api',
      pullRequestId: 4821,
      jobId: '11111111-1111-1111-1111-111111111111',
      filePath: 'src/Payments/RefundProcessor.cs',
      lineNumber: 214,
      severity: 'Error',
      message: 'The retry loop has no ceiling: a persistent 409 from the gateway will retry indefinitely.',
      coreTags: ['logic-error', 'resource-handling'],
      disposition: 'addressed',
      providerThreadId: '90412',
      observedAt: '2026-07-22T08:55:00Z',
    },
    {
      id: 'finding-2',
      clientId: '1',
      repositoryId: 'payments-api',
      pullRequestId: 4821,
      jobId: '11111111-1111-1111-1111-111111111111',
      filePath: 'src/Payments/LedgerWriter.cs',
      lineNumber: 63,
      severity: 'Warning',
      message: 'The ledger write and the balance update are not in one transaction.',
      coreTags: ['data-validation'],
      disposition: 'falsePositive',
      providerThreadId: '90415',
      observedAt: '2026-07-22T08:56:00Z',
    },
    {
      id: 'finding-3',
      clientId: '1',
      repositoryId: 'payments-api',
      pullRequestId: 4790,
      jobId: '22222222-2222-2222-2222-222222222222',
      filePath: 'src/Api/WebhookController.cs',
      lineNumber: 47,
      severity: 'Info',
      message: 'The webhook signature is compared with a non-constant-time equality check.',
      coreTags: ['security'],
      disposition: null,
      providerThreadId: null,
      observedAt: '2026-07-19T13:20:00Z',
    },
  ]

  return coreType ? all.filter((finding) => finding.coreTags.includes(coreType)) : all
}

export const handlers = [
  http.get(`${base}/auth/options`, async () => {
    return HttpResponse.json({
      edition: mockEdition,
      availableSignInMethods: mockSsoCapabilityAvailable ? ['password', 'sso'] : ['password'],
      capabilities: [getMockSsoCapability(), getMockBudgetingCapability(), getMockCodeInsightsCapability(), getMockMentionAnsweringCapability()],
    })
  }),

  http.patch(`${base}/admin/licensing/mock`, async ({ request }) => {
    const body = await request.json() as {
      edition?: string
      ssoAvailable?: boolean
    }

    if (body.edition === 'community' || body.edition === 'commercial') {
      mockEdition = body.edition
    }

    if (typeof body.ssoAvailable === 'boolean') {
      mockSsoCapabilityAvailable = body.ssoAvailable
    }

    persistMockLicensingState()

    return HttpResponse.json({
      edition: mockEdition,
      capabilities: [getMockSsoCapability(), getMockBudgetingCapability(), getMockCodeInsightsCapability(), getMockMentionAnsweringCapability()],
    })
  }),

  http.post(`${base}/auth/login`, async () => {
    await delay(500)
    // The real backend sets an httpOnly cookie. MSW's Set-Cookie isn't a real browser cookie
    // (per-page store, not shared across tabs), so the mock writes a real document.cookie instead
    // — this is what makes cross-tab session sharing demonstrable in the mock UI.
    setMockSessionCookie('admin')
    return HttpResponse.json({ accessToken: mockAdminAccessToken, expiresIn: 900, tokenType: 'Bearer' })
  }),

  http.post(`${base}/auth/refresh`, async () => {
    const session = readMockSessionCookie()
    if (!session) {
      return new HttpResponse(null, { status: 401 })
    }
    let accessToken: string
    switch (session) {
      case 'tenant':
        accessToken = mockTenantAccessToken
        break
      case 'sso':
        accessToken = mockTenantSsoAccessToken
        break
      default:
        accessToken = mockAdminAccessToken
    }
    return HttpResponse.json({ accessToken, expiresIn: 900, tokenType: 'Bearer' })
  }),

  http.post(`${base}/auth/logout`, async () => {
    clearMockSessionCookie()
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/auth/me`, async ({ request }) => {
    const payload = parseJwtPayload(request.headers.get('Authorization'))
    const isAdmin = payload?.global_role === 'Admin'
    const username = payload?.unique_name ?? ''

    return HttpResponse.json({
      globalRole: isAdmin ? 'Admin' : 'User',
      clientRoles: isAdmin ? { '1': 1, '2': 1 } : {},
      tenantRoles: isAdmin ? { 'tenant-1': 1 } : { 'tenant-1': 0 },
      hasLocalPassword: isAdmin || !username.includes('sso'),
      edition: mockEdition,
      capabilities: [getMockSsoCapability(), getMockBudgetingCapability(), getMockCodeInsightsCapability(), getMockMentionAnsweringCapability()],
    })
  }),

  http.get(`${base}/auth/tenants/:tenantSlug/providers`, async ({ params }) => {
    await delay(180)
    const tenantSlug = String(params.tenantSlug)
    const tenant = getMockTenantBySlug(tenantSlug)
    if (!tenant || tenant.isActive === false) {
      return HttpResponse.json({ error: 'Tenant sign-in is not available.' }, { status: 404 })
    }

    const providers = mockSsoCapabilityAvailable
      ? (mockTenantSsoProviders[tenant.id] ?? [])
        .filter((provider) => provider.isEnabled)
        .map((provider) => ({
          providerId: provider.id,
          displayName: provider.displayName,
          providerKind: provider.providerKind,
        }))
      : []

    return HttpResponse.json({
      tenantSlug: tenant.slug,
      localLoginEnabled: tenant.localLoginEnabled,
      providers,
    })
  }),

  http.post(`${base}/auth/tenants/:tenantSlug/local-login`, async ({ params }) => {
    await delay(220)
    const tenantSlug = String(params.tenantSlug)
    const tenant = getMockTenantBySlug(tenantSlug)
    if (!tenant?.localLoginEnabled) {
      return HttpResponse.json({ error: 'Local sign-in is disabled for this tenant.' }, { status: 401 })
    }

    setMockSessionCookie('tenant')
    return HttpResponse.json({ accessToken: mockTenantAccessToken, expiresIn: 900, tokenType: 'Bearer' })
  }),

  http.get(`${base}/auth/external/challenge/:tenantSlug/:providerId`, async ({ params, request }) => {
    await delay(160)

    if (!mockSsoCapabilityAvailable) {
      return createPremiumFeatureUnavailableResponse()
    }

    const tenantSlug = String(params.tenantSlug)
    const providerId = String(params.providerId)
    const tenant = getMockTenantBySlug(tenantSlug)
    const provider = tenant ? (mockTenantSsoProviders[tenant.id] ?? []).find((candidate) => candidate.id === providerId && candidate.isEnabled) : null

    if (!tenant || !provider) {
      return HttpResponse.json({ error: 'Provider not found.' }, { status: 404 })
    }

    const returnUrl = new URL(request.url).searchParams.get('returnUrl')
    if (returnUrl) {
      // Refresh token goes into the cookie, never the fragment; only the access token is in the hash.
      setMockSessionCookie('sso')
      return HttpResponse.redirect(`${returnUrl}#accessToken=${mockTenantSsoAccessToken}&expiresIn=900&tokenType=Bearer`)
    }

    return HttpResponse.redirect(`${base}/auth/external/callback/${tenant.slug}`)
  }),

  http.get(`${base}/auth/external/callback/:tenantSlug`, async ({ params }) => {
    await delay(180)

    if (!mockSsoCapabilityAvailable) {
      return createPremiumFeatureUnavailableResponse()
    }

    const tenantSlug = String(params.tenantSlug)
    const tenant = getMockTenantBySlug(tenantSlug)
    const provider = tenant ? (mockTenantSsoProviders[tenant.id] ?? []).find((candidate) => candidate.isEnabled) : null

    if (!tenant || !provider) {
      return HttpResponse.json({ error: 'Provider not found.' }, { status: 404 })
    }

    setMockSessionCookie('sso')
    return HttpResponse.json({ accessToken: mockTenantSsoAccessToken, expiresIn: 900, tokenType: 'Bearer' })
  }),

  http.get(`${base}/admin/tenants/:tenantId`, async ({ params }) => {
    await delay(180)
    const tenantId = String(params.tenantId)
    const tenant = getMockTenantById(tenantId)

    return tenant
      ? HttpResponse.json(tenant)
      : new HttpResponse(null, { status: 404 })
  }),

  http.patch(`${base}/admin/tenants/:tenantId`, async ({ params, request }) => {
    await delay(220)
    const tenantId = String(params.tenantId)
    const tenant = getMockTenantById(tenantId)
    if (!tenant) {
      return new HttpResponse(null, { status: 404 })
    }

    const body = await request.json() as any
    const updatedTenant = {
      ...tenant,
      displayName: body.displayName ?? tenant.displayName,
      isActive: body.isActive ?? tenant.isActive,
      localLoginEnabled: body.localLoginEnabled ?? tenant.localLoginEnabled,
      allowedAiProviderKinds: body.allowedAiProviderKinds ?? tenant.allowedAiProviderKinds ?? [],
      allowedAiEndpointHosts: body.allowedAiEndpointHosts ?? tenant.allowedAiEndpointHosts ?? [],
      updatedAt: new Date().toISOString(),
    }

    mockTenants = mockTenants.map((candidate) => candidate.id === tenantId ? updatedTenant : candidate)
    return HttpResponse.json(updatedTenant)
  }),

  http.get(`${base}/admin/tenants`, async () => {
    await delay(180)
    return HttpResponse.json(mockTenants)
  }),

  http.post(`${base}/admin/tenants`, async ({ request }) => {
    await delay(220)
    const body = await request.json() as any
    const created = {
      id: `tenant-${Math.random().toString(36).slice(2, 10)}`,
      slug: body.slug ?? 'new-tenant',
      displayName: body.displayName ?? 'New Tenant',
      isActive: true,
      localLoginEnabled: true,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      // A new tenant starts unrestricted, the same as one that never stated a policy.
      allowedAiProviderKinds: [] as string[],
      allowedAiEndpointHosts: [] as string[],
    }

    mockTenants = [...mockTenants, created]
    mockTenantSsoProviders[created.id] = []
    return HttpResponse.json(created, { status: 201 })
  }),

  http.get(`${base}/admin/tenants/:tenantId/sso-providers`, async ({ params }) => {
    await delay(180)
    const tenantId = String(params.tenantId)
    const tenant = getMockTenantById(tenantId)
    if (!tenant) {
      return new HttpResponse(null, { status: 404 })
    }

    if (!mockSsoCapabilityAvailable) {
      return createPremiumFeatureUnavailableResponse()
    }

    return HttpResponse.json(mockTenantSsoProviders[tenantId] ?? [])
  }),

  http.post(`${base}/admin/tenants/:tenantId/sso-providers`, async ({ params, request }) => {
    await delay(240)
    const tenantId = String(params.tenantId)
    const tenant = getMockTenantById(tenantId)
    if (!tenant) {
      return new HttpResponse(null, { status: 404 })
    }

    if (!mockSsoCapabilityAvailable) {
      return createPremiumFeatureUnavailableResponse()
    }

    const body = await request.json() as any
    const created = {
      id: `provider-${Math.random().toString(36).slice(2, 10)}`,
      tenantId,
      displayName: body.displayName ?? 'New provider',
      providerKind: body.providerKind ?? 'EntraId',
      protocolKind: body.protocolKind ?? 'Oidc',
      issuerOrAuthorityUrl: body.issuerOrAuthorityUrl ?? null,
      clientId: body.clientId ?? 'generated-client-id',
      secretConfigured: Boolean(body.clientSecret),
      scopes: body.scopes ?? [],
      allowedEmailDomains: body.allowedEmailDomains ?? [],
      isEnabled: body.isEnabled ?? true,
      autoCreateUsers: body.autoCreateUsers ?? true,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    }

    mockTenantSsoProviders[tenantId] = [...(mockTenantSsoProviders[tenantId] ?? []), created]
    return HttpResponse.json(created, { status: 201 })
  }),

  http.put(`${base}/admin/tenants/:tenantId/sso-providers/:providerId`, async ({ params, request }) => {
    await delay(240)
    const tenantId = String(params.tenantId)
    const providerId = String(params.providerId)
    const tenant = getMockTenantById(tenantId)
    if (!tenant) {
      return new HttpResponse(null, { status: 404 })
    }

    if (!mockSsoCapabilityAvailable) {
      return createPremiumFeatureUnavailableResponse()
    }

    const existing = (mockTenantSsoProviders[tenantId] ?? []).find((provider) => provider.id === providerId)
    if (!existing) {
      return new HttpResponse(null, { status: 404 })
    }

    const body = await request.json() as any
    const updated = {
      ...existing,
      displayName: body.displayName ?? existing.displayName,
      providerKind: body.providerKind ?? existing.providerKind,
      protocolKind: body.protocolKind ?? existing.protocolKind,
      issuerOrAuthorityUrl: body.issuerOrAuthorityUrl ?? null,
      clientId: body.clientId ?? existing.clientId,
      // A blank secret keeps the stored one; a new secret marks it configured.
      secretConfigured: body.clientSecret ? true : existing.secretConfigured,
      scopes: body.scopes ?? existing.scopes,
      allowedEmailDomains: body.allowedEmailDomains ?? existing.allowedEmailDomains,
      isEnabled: body.isEnabled ?? existing.isEnabled,
      autoCreateUsers: body.autoCreateUsers ?? existing.autoCreateUsers,
      updatedAt: new Date().toISOString(),
    }

    mockTenantSsoProviders[tenantId] = (mockTenantSsoProviders[tenantId] ?? []).map((provider) =>
      provider.id === providerId ? updated : provider,
    )
    return HttpResponse.json(updated)
  }),

  http.delete(`${base}/admin/tenants/:tenantId/sso-providers/:providerId`, async ({ params }) => {
    await delay(200)
    const tenantId = String(params.tenantId)
    const providerId = String(params.providerId)

    if (!getMockTenantById(tenantId)) {
      return new HttpResponse(null, { status: 404 })
    }

    if (!mockSsoCapabilityAvailable) {
      return createPremiumFeatureUnavailableResponse()
    }

    mockTenantSsoProviders[tenantId] = (mockTenantSsoProviders[tenantId] ?? []).filter((provider) => provider.id !== providerId)
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/admin/tenants/:tenantId/memberships`, async ({ params }) => {
    await delay(200)
    const tenantId = String(params.tenantId)
    if (!getMockTenantById(tenantId)) {
      return new HttpResponse(null, { status: 404 })
    }

    return HttpResponse.json([
      { id: 'mem-1', tenantId, userId: 'user-admin', username: 'admin', email: 'admin@acme.test', userIsActive: true, role: 'tenantAdministrator', assignedAt: '2026-06-01T10:00:00Z', updatedAt: '2026-06-01T10:00:00Z' },
      { id: 'mem-2', tenantId, userId: 'user-jsmith', username: 'jsmith', email: 'jsmith@acme.test', userIsActive: true, role: 'tenantUser', assignedAt: '2026-06-05T14:30:00Z', updatedAt: '2026-06-10T09:15:00Z' },
    ])
  }),

  http.patch(`${base}/admin/tenants/:tenantId/memberships/:membershipId`, async ({ params, request }) => {
    await delay(200)
    const tenantId = String(params.tenantId)
    const membershipId = String(params.membershipId)
    const body = await request.json() as { role?: string }
    return HttpResponse.json({ id: membershipId, tenantId, userId: 'user-jsmith', username: 'jsmith', email: 'jsmith@acme.test', userIsActive: true, role: body.role ?? 'tenantUser', assignedAt: '2026-06-05T14:30:00Z', updatedAt: '2026-06-14T12:00:00Z' })
  }),

  http.delete(`${base}/admin/tenants/:tenantId/memberships/:membershipId`, async () => {
    await delay(200)
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/clients`, async () => {
    await delay(300)
    return HttpResponse.json([
      buildMockClient('1', 'Acme Corp', { tenantId: 'tenant-1', tenantDisplayName: 'Acme Corp', tenantSlug: 'acme' }),
      buildMockClient('2', 'Globex Inc', { isActive: false, recentUsageTokens: 0, tenantId: 'tenant-1', tenantDisplayName: 'Acme Corp', tenantSlug: 'acme' }),
      buildMockClient('3', 'Umbrella Corp', { recentUsageTokens: 89300 }),
    ])
  }),

  http.get(`${base}/clients/:id`, async ({ params }) => {
    await delay(300)
    const id = String(params.id)
    return HttpResponse.json(buildMockClient(id))
  }),

  http.patch(`${base}/clients/:id`, async ({ params, request }) => {
    await delay(300)
    const id = String(params.id)
    const body = await request.json() as any
    patchedClientFields[id] = { ...(patchedClientFields[id] ?? {}), ...body }
    return HttpResponse.json(buildMockClient(id, body.displayName ?? `Mocked Client ${id}`, body))
  }),

  http.get(`${base}/admin/clients/:clientId/budget/consumption`, async ({ params, request }) => {
    await delay(300)
    const period = new URL(request.url).searchParams.get('period')
    return HttpResponse.json(buildMockBudgetConsumption(String(params.clientId), period))
  }),

  http.post(`${base}/admin/clients/:clientId/budget/reset`, async ({ params }) => {
    await delay(300)
    const reset = grantMockSpendReset(String(params.clientId))
    // An uncapped client has no ceiling to raise, which the real endpoint reports as a 400.
    return reset === null
      ? HttpResponse.json(
        { error: 'The client has no monthly budget cap configured, so there is no allowance to top up.' },
        { status: 400 },
      )
      : HttpResponse.json(reset)
  }),

  http.get(`${base}/admin/clients/:clientId/budget/history`, async ({ params, request }) => {
    await delay(300)
    const months = Number(new URL(request.url).searchParams.get('months') ?? '12')
    return HttpResponse.json(buildMockBudgetHistory(String(params.clientId), Number.isFinite(months) ? months : 12))
  }),

  http.get(`${base}/admin/tenants/:tenantId/budget/overview`, async ({ params }) => {
    await delay(300)
    return HttpResponse.json(buildMockTenantBudgetOverview(String(params.tenantId)))
  }),

  http.get(`${base}/admin/tenants/:tenantId/budget/spend`, async ({ params, request }) => {
    await delay(300)
    const months = Number(new URL(request.url).searchParams.get('months') ?? '12')
    return HttpResponse.json(buildMockTenantSpend(String(params.tenantId), Number.isFinite(months) ? months : 12))
  }),

  http.get(`${base}/admin/review-profiles`, async () => {
    await delay(150)
    return HttpResponse.json({ profiles: reviewProfiles })
  }),

  http.get(`${base}/admin/clients/:clientId/review-profile`, async ({ params }) => {
    await delay(150)
    return HttpResponse.json(getEffectiveReviewProfile(String(params.clientId)))
  }),

  http.put(`${base}/admin/clients/:clientId/review-profile`, async ({ params, request }) => {
    await delay(200)
    const clientId = String(params.clientId)
    const body = await request.json() as any
    const requestedProfileId = typeof body?.defaultReviewPipelineProfileId === 'string'
      ? body.defaultReviewPipelineProfileId
      : null

    if (requestedProfileId && !reviewProfiles.some((profile) => profile.profileId === requestedProfileId)) {
      return HttpResponse.json({ title: 'Unknown review profile.' }, { status: 400 })
    }

    clientReviewProfiles[clientId] = {
      defaultReviewPipelineProfileId: requestedProfileId,
      updatedAtUtc: requestedProfileId ? new Date().toISOString() : null,
    }

    return HttpResponse.json(getEffectiveReviewProfile(clientId))
  }),

  http.get(`${base}/clients/:clientId/ado-organization-scopes`, async ({ params }) => {
    await delay(250)
    const clientId = String(params.clientId)
    return HttpResponse.json(adoOrganizationScopesByClient[clientId] ?? [])
  }),

  http.post(`${base}/clients/:clientId/ado-organization-scopes`, async ({ params, request }) => {
    await delay(400)
    const clientId = String(params.clientId)
    const body = await request.json() as any
    const newScope = {
      id: `scope-${Math.random().toString(36).slice(2, 10)}`,
      clientId,
      organizationUrl: body.organizationUrl ?? '',
      displayName: body.displayName ?? null,
      isEnabled: body.isEnabled ?? true,
      verificationStatus: 'pending',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    }
    adoOrganizationScopesByClient[clientId] = [...(adoOrganizationScopesByClient[clientId] ?? []), newScope]
    return HttpResponse.json(newScope, { status: 201 })
  }),

  http.patch(`${base}/clients/:clientId/ado-organization-scopes/:scopeId`, async ({ params, request }) => {
    await delay(300)
    const clientId = String(params.clientId)
    const scopeId = String(params.scopeId)
    const body = await request.json() as any
    const scopes = adoOrganizationScopesByClient[clientId] ?? []
    const idx = scopes.findIndex(s => s.id === scopeId)
    if (idx === -1) return new HttpResponse(null, { status: 404 })
    scopes[idx] = { ...scopes[idx], ...body, updatedAt: new Date().toISOString() }
    return HttpResponse.json(scopes[idx])
  }),

  http.delete(`${base}/clients/:clientId/ado-organization-scopes/:scopeId`, async ({ params }) => {
    await delay(300)
    const clientId = String(params.clientId)
    const scopeId = String(params.scopeId)
    adoOrganizationScopesByClient[clientId] = (adoOrganizationScopesByClient[clientId] ?? []).filter(s => s.id !== scopeId)
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/admin/clients/:clientId/ado/discovery/projects`, async ({ params, request }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const url = new URL(request.url)
    const organizationScopeId = url.searchParams.get('organizationScopeId')
    const scope = getScope(clientId, organizationScopeId)

    if (!scope || scope.isEnabled === false) {
      return HttpResponse.json({ error: 'The selected Azure DevOps organization is no longer available for this client.' }, { status: 409 })
    }

    return HttpResponse.json(adoProjectsByScope[scope.id] ?? [])
  }),

  http.get(`${base}/admin/clients/:clientId/ado/discovery/crawl-filters`, async ({ params, request }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const url = new URL(request.url)
    const organizationScopeId = url.searchParams.get('organizationScopeId')
    const projectId = url.searchParams.get('projectId')
    const scope = getScope(clientId, organizationScopeId)

    if (!scope || scope.isEnabled === false) {
      return HttpResponse.json({ error: 'The selected Azure DevOps organization is no longer available for this client.' }, { status: 409 })
    }

    return HttpResponse.json(getCrawlFilters(scope.id, projectId))
  }),

  // Provider-neutral discovery, which the mention configuration form drives for every provider other than
  // Azure DevOps. Keyed on the connection, because that is where the host comes from.
  http.get(`${base}/admin/clients/:clientId/providers/:provider/discovery/scopes`, async ({ request }) => {
    await delay(200)
    const connectionId = new URL(request.url).searchParams.get('connectionId')

    if (!connectionId) {
      return HttpResponse.json({ error: 'That connection does not belong to this client.' }, { status: 400 })
    }

    return HttpResponse.json([
      { scopePath: 'meister-dev', displayName: 'meister-dev' },
      { scopePath: 'acme', displayName: 'acme' },
    ])
  }),

  http.get(`${base}/admin/clients/:clientId/providers/:provider/discovery/repositories`, async ({ request }) => {
    await delay(200)
    const url = new URL(request.url)
    const connectionId = url.searchParams.get('connectionId')
    const scopePath = url.searchParams.get('scopePath')

    if (!connectionId || !scopePath) {
      return HttpResponse.json({ error: 'That connection does not belong to this client.' }, { status: 400 })
    }

    return HttpResponse.json([
      { repositoryId: '101', displayName: `${scopePath}/propr`, scopePath },
      { repositoryId: '102', displayName: `${scopePath}/propr-website`, scopePath },
    ])
  }),

  http.get(`${base}/admin/clients/:clientId/ado/discovery/sources`, async ({ params, request }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const url = new URL(request.url)
    const organizationScopeId = url.searchParams.get('organizationScopeId')
    const projectId = url.searchParams.get('projectId')
    const sourceKind = url.searchParams.get('sourceKind') ?? 'repository'
    const scope = getScope(clientId, organizationScopeId)

    if (!scope || scope.isEnabled === false) {
      return HttpResponse.json({ error: 'The selected Azure DevOps organization is no longer available for this client.' }, { status: 409 })
    }

    const key = `${scope.id}::${projectId}::${sourceKind}`
    return HttpResponse.json(adoSourcesByProject[key] ?? [])
  }),

  http.get(`${base}/admin/clients/:clientId/ado/discovery/branches`, async ({ params, request }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const url = new URL(request.url)
    const organizationScopeId = url.searchParams.get('organizationScopeId')
    const canonicalSourceValue = url.searchParams.get('canonicalSourceValue')
    const scope = getScope(clientId, organizationScopeId)

    if (!scope || scope.isEnabled === false) {
      return HttpResponse.json({ error: 'The selected Azure DevOps organization is no longer available for this client.' }, { status: 409 })
    }

    return HttpResponse.json(adoBranchesBySource[canonicalSourceValue ?? ''] ?? [])
  }),

  http.get(`${base}/admin/clients/:clientId/procursor/sources`, async ({ params }) => {
    await delay(300)
    const clientId = String(params.clientId)
    return HttpResponse.json(proCursorSourcesByClient[clientId] ?? [])
  }),

  http.post(`${base}/admin/clients/:clientId/procursor/sources`, async ({ params, request }) => {
    await delay(500)
    const clientId = String(params.clientId)
    const body = await request.json() as any
    const scope = getScope(clientId, body.organizationScopeId)

    if (body.organizationScopeId && (!scope || scope.isEnabled === false)) {
      return HttpResponse.json({ error: 'The selected Azure DevOps organization is no longer available for this client.' }, { status: 409 })
    }

    const newSource = {
      sourceId: `src-${Math.random().toString(36).slice(2, 10)}`,
      clientId,
      organizationScopeId: body.organizationScopeId ?? null,
      providerScopePath: scope?.organizationUrl ?? null,
      providerProjectKey: body.providerProjectKey ?? null,
      repositoryId: body.canonicalSourceRef?.value ?? null,
      sourceDisplayName: body.sourceDisplayName ?? null,
      canonicalSourceRef: body.canonicalSourceRef ?? null,
      displayName: body.displayName ?? 'New Source',
      sourceKind: body.sourceKind ?? 'repository',
      defaultBranch: body.defaultBranch ?? 'main',
      rootPath: body.rootPath ?? null,
      symbolMode: body.symbolMode ?? 'auto',
      isEnabled: true,
      status: 'pending',
      latestSnapshot: null,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    }

    proCursorSourcesByClient[clientId] = [newSource, ...(proCursorSourcesByClient[clientId] ?? [])]
    return HttpResponse.json(newSource, { status: 201 })
  }),

  http.post(`${base}/admin/clients/:clientId/procursor/sources/:sourceId/refresh`, async ({ params }) => {
    await delay(300)
    const clientId = String(params.clientId)
    const sourceId = String(params.sourceId)
    const sources = proCursorSourcesByClient[clientId] ?? []
    const source = sources.find(s => s.sourceId === sourceId)

    if (!source) {
      return new HttpResponse(null, { status: 404 })
    }

    return HttpResponse.json({ sourceId, status: 'queued', queuedAt: new Date().toISOString() })
  }),

  http.get(`${base}/admin/clients/:clientId/procursor/sources/:sourceId/branches`, async ({ params }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const sourceId = String(params.sourceId)
    const sources = proCursorSourcesByClient[clientId] ?? []
    const source = sources.find(s => s.sourceId === sourceId)

    if (!source) return new HttpResponse(null, { status: 404 })

    const branches = adoBranchesBySource[source.repositoryId] ?? []
    return HttpResponse.json(
      branches.map((b: any, i: number) => ({
        branchId: `branch-${sourceId}-${i}`,
        sourceId,
        branchName: b.branchName,
        isDefault: b.isDefault ?? false,
        autoRefreshEnabled: b.isDefault ?? false,
        createdAt: new Date(Date.now() - 86400000 * (i + 1)).toISOString(),
        updatedAt: new Date(Date.now() - 86400000 * (i + 1)).toISOString(),
      }))
    )
  }),

  http.post(`${base}/admin/clients/:clientId/procursor/sources/:sourceId/branches`, async ({ params, request }) => {
    await delay(300)
    const body = await request.json() as any
    const sourceId = String(params.sourceId)
    const newBranch = {
      branchId: `branch-${Math.random().toString(36).slice(2, 10)}`,
      sourceId,
      branchName: body.branchName ?? 'main',
      isDefault: body.isDefault ?? false,
      autoRefreshEnabled: body.autoRefreshEnabled ?? false,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    }
    return HttpResponse.json(newBranch, { status: 201 })
  }),

  http.patch(`${base}/admin/clients/:clientId/procursor/sources/:sourceId/branches/:branchId`, async ({ params, request }) => {
    await delay(250)
    const body = await request.json() as any
    return HttpResponse.json({
      branchId: String(params.branchId),
      sourceId: String(params.sourceId),
      branchName: body.branchName ?? 'main',
      isDefault: body.isDefault ?? false,
      autoRefreshEnabled: body.autoRefreshEnabled ?? false,
      updatedAt: new Date().toISOString(),
    })
  }),

  http.delete(`${base}/admin/clients/:clientId/procursor/sources/:sourceId/branches/:branchId`, async () => {
    await delay(250)
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/admin/clients/:clientId/procursor/token-usage`, async ({ params }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const usage = proCursorClientUsageByClient[clientId]

    if (!usage) {
      return HttpResponse.json({ error: 'Failed to load ProCursor usage.' }, { status: 404 })
    }

    return HttpResponse.json({
      ...usage,
      topSources: proCursorTopSourcesByClient[clientId] ?? usage.topSources ?? [],
    })
  }),

  http.get(`${base}/admin/clients/:clientId/procursor/token-usage/top-sources`, async ({ params }) => {
    await delay(180)
    const clientId = String(params.clientId)
    return HttpResponse.json({ items: proCursorTopSourcesByClient[clientId] ?? [] })
  }),

  http.get(`${base}/admin/clients/:clientId/procursor/sources/:sourceId/token-usage`, async ({ params }) => {
    await delay(220)
    const sourceId = String(params.sourceId)
    const usage = proCursorSourceUsageBySource[sourceId]

    if (!usage) {
      return HttpResponse.json({ error: 'Failed to load source-level ProCursor usage.' }, { status: 404 })
    }

    return HttpResponse.json(usage)
  }),

  http.get(`${base}/admin/clients/:clientId/procursor/sources/:sourceId/token-usage/events`, async ({ params, request }) => {
    await delay(220)
    const sourceId = String(params.sourceId)
    const url = new URL(request.url)
    const limit = Number(url.searchParams.get('limit') ?? '10')
    const items = proCursorRecentEventsBySource[sourceId] ?? []
    return HttpResponse.json({ items: items.slice(0, limit) })
  }),

  http.get(`${base}/clients/:clientId/ai-connections`, async ({ params }) => {
    await delay(250)
    const clientId = String(params.clientId)
    return HttpResponse.json(aiConnectionsByClient[clientId] ?? [])
  }),

  // What this client may configure: every family this build has a driver for, each flagged with whether the
  // tenant permits it, plus the wire shapes its driver speaks. Mirrors the server, which is authoritative for
  // all three — the UI must not offer a family it cannot call or a shape that cannot be spoken.
  http.get(`${base}/clients/:clientId/ai-connections/permitted-providers`, async () => {
    await delay(160)
    const allowed = mockTenants[0]?.allowedAiProviderKinds ?? []

    return HttpResponse.json({
      isRestricted: allowed.length > 0,
      providers: mockDriverProtocolModes.map(([providerKind, protocolModes]) => ({
        providerKind,
        isPermitted: allowed.length === 0 || allowed.includes(providerKind),
        protocolModes,
      })),
    })
  }),

  // Probing an unsaved profile. The refusal arm is reachable so the failure path can be seen without a provider:
  // a base URL that is not https is what every driver rejects first.
  http.post(`${base}/clients/:clientId/ai-connections/probe`, async ({ request }) => {
    await delay(420)
    const body = await request.json() as any
    const baseUrl = String(body.baseUrl ?? '')

    if (!baseUrl.startsWith('https://')) {
      return HttpResponse.json({
        status: 'failed',
        failureCategory: 'configuration',
        summary: 'baseUrl must use https.',
        actionHint: 'Correct the base URL and test again.',
        checkedAt: new Date().toISOString(),
        warnings: [],
      })
    }

    return HttpResponse.json({
      status: 'verified',
      summary: `Verified connectivity for '${baseUrl}'.`,
      checkedAt: new Date().toISOString(),
      warnings: [],
    })
  }),

  // ---- Model catalog (browse-and-pick) ----
  // Client and tenant scopes read the same global rows; the difference is which overrides are applied, so the
  // mock serves one dataset to both and labels the pricing layer per entry.
  http.get(`${base}/clients/:clientId/model-catalog/providers`, async () => {
    await delay(140)
    return HttpResponse.json(mockCatalogProviders)
  }),

  http.get(`${base}/tenants/:tenantId/model-catalog/providers`, async () => {
    await delay(140)
    return HttpResponse.json(mockCatalogProviders)
  }),

  http.get(`${base}/clients/:clientId/model-catalog/models`, async ({ request }) => {
    await delay(200)
    return HttpResponse.json(filterCatalogModels(request))
  }),

  http.get(`${base}/tenants/:tenantId/model-catalog/models`, async ({ request }) => {
    await delay(200)
    return HttpResponse.json(filterCatalogModels(request))
  }),

  // The override endpoints. Unhandled, they fell through to the dev proxy and came back 502 — the tenant settings
  // page called one on every load, so the screen reported "no overrides" for a request that never reached a
  // server, and any failure handling downstream was reacting to a proxy error rather than to an answer.
  http.get(`${base}/tenants/:tenantId/model-catalog/overrides`, async () => {
    await delay(160)
    return HttpResponse.json(mockCatalogOverrides)
  }),

  http.put(`${base}/tenants/:tenantId/model-catalog/overrides`, async ({ request }) => {
    await delay(240)
    const body = await request.json() as any
    const key = `${body.providerId}:${body.remoteModelId}`
    mockCatalogOverrides = [
      ...mockCatalogOverrides.filter((entry) => `${entry.providerId}:${entry.remoteModelId}` !== key),
      { ...body, pricingLayer: 'tenant' },
    ]
    return new HttpResponse(null, { status: 204 })
  }),

  http.delete(`${base}/tenants/:tenantId/model-catalog/overrides/:providerId/:remoteModelId`, async ({ params }) => {
    await delay(200)
    const key = `${String(params.providerId)}:${String(params.remoteModelId)}`
    mockCatalogOverrides = mockCatalogOverrides.filter((entry) => `${entry.providerId}:${entry.remoteModelId}` !== key)
    return new HttpResponse(null, { status: 204 })
  }),

  // Defining a model the catalog does not list. The real endpoint refuses one the catalog already describes,
  // which is the case the UI has a message for, so the mock refuses it too.
  http.put(`${base}/tenants/:tenantId/model-catalog/models`, async ({ request }) => {
    await delay(260)
    const body = await request.json() as any
    const known = mockCatalogModels.some(
      (model) => model.providerId === body.providerId && model.remoteModelId === body.remoteModelId,
    )

    if (known) {
      return HttpResponse.json(
        { detail: 'The catalog already describes this model. Record a pricing override instead.' },
        { status: 409 },
      )
    }

    mockCatalogModels.push(mockCatalogEntry(
      body.providerId,
      body.providerId,
      body.remoteModelId,
      body.displayName || body.remoteModelId,
      Number(body.inputCostPer1MUsd) || 0,
      Number(body.outputCostPer1MUsd) || 0,
    ))
    return new HttpResponse(null, { status: 204 })
  }),

  http.post(`${base}/clients/:clientId/ai-connections/discover-models`, async ({ request }) => {
    await delay(500)
    const body = await request.json() as any
    const providerKind = String(body.providerKind ?? 'azureOpenAi')
    const discovered = mockDiscoveredModels[providerKind] ?? mockDiscoveredModels.azureOpenAi

    return HttpResponse.json({
      discoveryStatus: 'succeeded',
      manualEntryAllowed: true,
      warnings: mockDiscoveryWarnings[providerKind] ?? [],
      models: discovered,
    })
  }),

  http.post(`${base}/clients/:clientId/ai-connections`, async ({ params, request }) => {
    await delay(300)
    const clientId = String(params.clientId)
    const body = await request.json() as any
    const newConnection = {
      id: `ai-${Math.random().toString(36).slice(2, 10)}`,
      clientId,
      displayName: body.displayName ?? 'New connection',
      endpointUrl: body.endpointUrl ?? '',
      models: body.models ?? [],
      isActive: false,
      activeModel: null,
      modelCategory: body.modelCategory ?? null,
      modelCapabilities: body.modelCapabilities ?? [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    }

    aiConnectionsByClient[clientId] = [newConnection, ...(aiConnectionsByClient[clientId] ?? [])]
    return HttpResponse.json(newConnection, { status: 201 })
  }),

  http.patch(`${base}/clients/:clientId/ai-connections/:connectionId`, async ({ params, request }) => {
    await delay(300)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const body = await request.json() as any
    const connections = aiConnectionsByClient[clientId] ?? []
    const idx = connections.findIndex((connection) => connection.id === connectionId)

    if (idx === -1) {
      return new HttpResponse(null, { status: 404 })
    }

    connections[idx] = {
      ...connections[idx],
      displayName: body.displayName ?? connections[idx].displayName,
      endpointUrl: body.endpointUrl ?? connections[idx].endpointUrl,
      models: body.models ?? connections[idx].models,
      modelCapabilities: body.modelCapabilities ?? connections[idx].modelCapabilities,
      updatedAt: new Date().toISOString(),
    }

    return HttpResponse.json(connections[idx])
  }),

  http.post(`${base}/clients/:clientId/ai-connections/:connectionId/activate`, async ({ params, request }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const body = await request.json() as any
    const connections = aiConnectionsByClient[clientId] ?? []
    const idx = connections.findIndex((connection) => connection.id === connectionId)

    if (idx === -1) {
      return new HttpResponse(null, { status: 404 })
    }

    connections[idx] = {
      ...connections[idx],
      isActive: connections[idx].modelCategory ? connections[idx].isActive : true,
      activeModel: body.model,
      updatedAt: new Date().toISOString(),
    }

    return HttpResponse.json(connections[idx])
  }),

  http.post(`${base}/clients/:clientId/ai-connections/:connectionId/deactivate`, async ({ params }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const connections = aiConnectionsByClient[clientId] ?? []
    const idx = connections.findIndex((connection) => connection.id === connectionId)

    if (idx === -1) {
      return new HttpResponse(null, { status: 404 })
    }

    connections[idx] = {
      ...connections[idx],
      isActive: false,
      activeModel: null,
      updatedAt: new Date().toISOString(),
    }

    return HttpResponse.json(connections[idx])
  }),

  http.delete(`${base}/clients/:clientId/ai-connections/:connectionId`, async ({ params }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const connections = aiConnectionsByClient[clientId] ?? []
    aiConnectionsByClient[clientId] = connections.filter((connection) => connection.id !== connectionId)
    return new HttpResponse(null, { status: 204 })
  }),

  // ---- Logical models: per-client overrides + effective list ----

  http.get(`${base}/clients/:clientId/logical-models`, async () => {
    await delay(150)
    return HttpResponse.json(effectiveLogicalModels())
  }),

  http.get(`${base}/clients/:clientId/logical-models/overrides`, async () => {
    await delay(150)
    return HttpResponse.json(mockClientLogicalOverrides)
  }),

  http.post(`${base}/clients/:clientId/logical-models/overrides`, async ({ request }) => {
    await delay(250)
    const created = logicalModelFromBody(await request.json(), 'client')
    mockClientLogicalOverrides = [...mockClientLogicalOverrides, created]
    return HttpResponse.json(created, { status: 201 })
  }),

  http.put(`${base}/clients/:clientId/logical-models/overrides/:name`, async ({ params, request }) => {
    await delay(200)
    const name = String(params.name)
    const body = await request.json() as any
    mockClientLogicalOverrides = mockClientLogicalOverrides.map((entry) =>
      entry.name === name ? { ...entry, ...updatedMapping(entry, body) } : entry,
    )
    return new HttpResponse(null, { status: 204 })
  }),

  http.delete(`${base}/clients/:clientId/logical-models/overrides/:name`, async ({ params }) => {
    await delay(200)
    const name = String(params.name)
    mockClientLogicalOverrides = mockClientLogicalOverrides.filter((entry) => entry.name !== name)
    return new HttpResponse(null, { status: 204 })
  }),

  // ---- Logical models: per-client purpose map ----

  http.get(`${base}/clients/:clientId/logical-models/purposes`, async () => {
    await delay(150)
    return HttpResponse.json(
      Object.entries(mockClientPurposeRoles).map(([purpose, logicalModelName]) => ({ purpose, logicalModelName })),
    )
  }),

  http.put(`${base}/clients/:clientId/logical-models/purposes/:purpose`, async ({ params, request }) => {
    await delay(200)
    const purpose = String(params.purpose)
    const body = await request.json() as any
    mockClientPurposeRoles = { ...mockClientPurposeRoles, [purpose]: body.logicalModelName }
    return new HttpResponse(null, { status: 204 })
  }),

  http.delete(`${base}/clients/:clientId/logical-models/purposes/:purpose`, async ({ params }) => {
    await delay(200)
    const purpose = String(params.purpose)
    const { [purpose]: _removed, ...rest } = mockClientPurposeRoles
    mockClientPurposeRoles = rest
    return new HttpResponse(null, { status: 204 })
  }),

  // ---- Logical models: tenant catalog + tenant connections ----

  http.get(`${base}/tenants/:tenantId/logical-models`, async () => {
    await delay(150)
    return HttpResponse.json(mockTenantLogicalModels)
  }),

  http.post(`${base}/tenants/:tenantId/logical-models`, async ({ request }) => {
    await delay(250)
    const created = logicalModelFromBody(await request.json(), 'tenant')
    mockTenantLogicalModels = [...mockTenantLogicalModels, created]
    return HttpResponse.json(created, { status: 201 })
  }),

  http.put(`${base}/tenants/:tenantId/logical-models/:name`, async ({ params, request }) => {
    await delay(200)
    const name = String(params.name)
    const body = await request.json() as any
    mockTenantLogicalModels = mockTenantLogicalModels.map((entry) =>
      entry.name === name ? { ...entry, ...updatedMapping(entry, body) } : entry,
    )
    return new HttpResponse(null, { status: 204 })
  }),

  http.post(`${base}/tenants/:tenantId/logical-models/:name/rename`, async ({ params, request }) => {
    await delay(200)
    const name = String(params.name)
    const body = await request.json() as any
    mockTenantLogicalModels = mockTenantLogicalModels.map((entry) =>
      entry.name === name ? { ...entry, name: body.newName } : entry,
    )
    return new HttpResponse(null, { status: 204 })
  }),

  http.delete(`${base}/tenants/:tenantId/logical-models/:name`, async ({ params }) => {
    await delay(200)
    const name = String(params.name)
    mockTenantLogicalModels = mockTenantLogicalModels.filter((entry) => entry.name !== name)
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/tenants/:tenantId/ai-connections`, async () => {
    await delay(150)
    return HttpResponse.json(mockTenantConnections)
  }),

  http.post(`${base}/tenants/:tenantId/ai-connections`, async ({ params, request }) => {
    await delay(300)
    const body = await request.json() as any
    const created = {
      id: `tc-${Math.random().toString(36).slice(2, 10)}`,
      tenantId: String(params.tenantId),
      clientId: null,
      displayName: body.displayName ?? 'New tenant connection',
      providerKind: body.providerKind ?? 'openAi',
      baseUrl: body.baseUrl ?? '',
      authMode: body.auth?.mode ?? 'apiKey',
      discoveryMode: body.discoveryMode ?? 'manualOnly',
      isActive: false,
      configuredModels: (body.configuredModels ?? []).map((model: any) => ({
        id: `tcm-${Math.random().toString(36).slice(2, 10)}`,
        displayName: model.displayName || model.remoteModelId,
        remoteModelId: model.remoteModelId,
        supportsChat: !(model.operationKinds ?? ['chat']).includes('embedding'),
        supportsEmbedding: (model.operationKinds ?? []).includes('embedding'),
      })),
      purposeBindings: [],
      verification: { status: 'unverified', summary: null },
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    }
    mockTenantConnections = [created, ...mockTenantConnections]
    return HttpResponse.json(created, { status: 201 })
  }),

  http.delete(`${base}/tenants/:tenantId/ai-connections/:connectionId`, async ({ params }) => {
    await delay(200)
    const connectionId = String(params.connectionId)
    mockTenantConnections = mockTenantConnections.filter((connection) => connection.id !== connectionId)
    return new HttpResponse(null, { status: 204 })
  }),

  http.post(`${base}/tenants/:tenantId/ai-connections/:connectionId/verify`, async ({ params }) => {
    await delay(300)
    const connectionId = String(params.connectionId)
    mockTenantConnections = mockTenantConnections.map((connection) =>
      connection.id === connectionId
        ? { ...connection, verification: { status: 'verified', summary: 'Verified against the provider catalog.' } }
        : connection,
    )
    return HttpResponse.json({ status: 'verified', summary: 'Verified against the provider catalog.' })
  }),

  http.get(`${base}/admin/providers`, async () => {
    await delay(180)
    return HttpResponse.json(providerActivationStatuses)
  }),

  http.patch(`${base}/admin/providers/:provider`, async ({ params, request }) => {
    await delay(220)
    const providerFamily = String(params.provider)
    const body = await request.json() as any
    const index = providerActivationStatuses.findIndex((status) => status.providerFamily === providerFamily)

    if (index === -1) {
      return new HttpResponse(null, { status: 404 })
    }

    providerActivationStatuses[index] = {
      ...providerActivationStatuses[index],
      isEnabled: body.isEnabled !== false,
      updatedAt: new Date().toISOString(),
    }

    return HttpResponse.json(providerActivationStatuses[index])
  }),

  http.get(`${base}/clients/:clientId/provider-connections`, async ({ params }) => {
    await delay(220)
    const clientId = String(params.clientId)
    return HttpResponse.json((providerConnectionsByClient[clientId] ?? []).filter((connection) => isProviderEnabled(connection.providerFamily)))
  }),

  http.post(`${base}/clients/:clientId/provider-connections`, async ({ params, request }) => {
    await delay(280)
    const clientId = String(params.clientId)
    const body = await request.json() as any

    if (!isProviderEnabled(body.providerFamily)) {
      return HttpResponse.json({ error: 'The selected provider family is currently disabled by system administration.' }, { status: 409 })
    }

    const connection = {
      id: `provider-conn-${Math.random().toString(36).slice(2, 10)}`,
      clientId,
      providerFamily: body.providerFamily ?? 'github',
      hostBaseUrl: body.hostBaseUrl ?? 'https://github.com',
      authenticationKind: body.authenticationKind ?? 'personalAccessToken',
      userName: body.authenticationKind === 'windowsUserAccount' ? body.userName ?? null : null,
      gitHubAppId: body.authenticationKind === 'appInstallation' ? body.gitHubAppId ?? null : null,
      gitHubAppInstallationId: body.authenticationKind === 'appInstallation' ? body.gitHubAppInstallationId ?? null : null,
      displayName: body.displayName ?? 'New provider connection',
      isActive: body.isActive ?? true,
      verificationStatus: 'verified',
      lastVerifiedAt: new Date().toISOString(),
      lastVerificationError: null,
      lastVerificationFailureCategory: null,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    }

    providerConnectionsByClient[clientId] = [connection, ...(providerConnectionsByClient[clientId] ?? [])]
    providerScopesByConnection[connection.id] = []
    providerReviewerIdentitiesByConnection[connection.id] = null
    providerReviewerIdentityCandidatesByConnection[connection.id] = []

    return HttpResponse.json(connection, { status: 201 })
  }),

  http.get(`${base}/clients/:clientId/provider-operations/audit-trail`, async ({ params, request }) => {
    await delay(180)
    const clientId = String(params.clientId)
    const url = new URL(request.url)
    const take = Math.max(1, Number(url.searchParams.get('take') ?? '20'))

    return HttpResponse.json(buildProviderAuditTrail(clientId).slice(0, take))
  }),

  http.patch(`${base}/clients/:clientId/provider-connections/:connectionId`, async ({ params, request }) => {
    await delay(260)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const body = await request.json() as any
    const connections = providerConnectionsByClient[clientId] ?? []
    const index = connections.findIndex((connection) => connection.id === connectionId)

    if (index === -1) {
      return new HttpResponse(null, { status: 404 })
    }

    connections[index] = {
      ...connections[index],
      displayName: body.displayName ?? connections[index].displayName,
      hostBaseUrl: body.hostBaseUrl ?? connections[index].hostBaseUrl,
      authenticationKind: body.authenticationKind ?? connections[index].authenticationKind,
      userName: Object.prototype.hasOwnProperty.call(body, 'userName')
        ? body.userName
        : connections[index].userName,
      gitHubAppId: Object.prototype.hasOwnProperty.call(body, 'gitHubAppId')
        ? body.gitHubAppId
        : connections[index].gitHubAppId,
      gitHubAppInstallationId: Object.prototype.hasOwnProperty.call(body, 'gitHubAppInstallationId')
        ? body.gitHubAppInstallationId
        : connections[index].gitHubAppInstallationId,
      isActive: body.isActive ?? connections[index].isActive,
      updatedAt: new Date().toISOString(),
    }

    return HttpResponse.json(connections[index])
  }),

  http.delete(`${base}/clients/:clientId/provider-connections/:connectionId`, async ({ params }) => {
    await delay(220)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)

    providerConnectionsByClient[clientId] = (providerConnectionsByClient[clientId] ?? [])
      .filter((connection) => connection.id !== connectionId)
    delete providerScopesByConnection[connectionId]
    delete providerReviewerIdentitiesByConnection[connectionId]
    delete providerReviewerIdentityCandidatesByConnection[connectionId]

    return new HttpResponse(null, { status: 204 })
  }),

  http.post(`${base}/clients/:clientId/provider-connections/:connectionId/verify`, async ({ params }) => {
    await delay(220)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const connection = getProviderConnection(clientId, connectionId)

    if (!connection) {
      return new HttpResponse(null, { status: 404 })
    }

    connection.verificationStatus = 'verified'
    connection.lastVerifiedAt = new Date().toISOString()
    connection.lastVerificationError = null
    connection.lastVerificationFailureCategory = null
    connection.updatedAt = new Date().toISOString()

    return HttpResponse.json(connection)
  }),

  http.get(`${base}/clients/:clientId/provider-connections/:connectionId/scopes`, async ({ params }) => {
    await delay(220)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)

    if (!getProviderConnection(clientId, connectionId)) {
      return new HttpResponse(null, { status: 404 })
    }

    return HttpResponse.json(providerScopesByConnection[connectionId] ?? [])
  }),

  http.post(`${base}/clients/:clientId/provider-connections/:connectionId/scopes`, async ({ params, request }) => {
    await delay(260)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const connection = getProviderConnection(clientId, connectionId)

    if (!connection) {
      return new HttpResponse(null, { status: 404 })
    }

    const body = await request.json() as any
    const scope = {
      id: `provider-scope-${Math.random().toString(36).slice(2, 10)}`,
      clientId,
      connectionId,
      scopeType: body.scopeType ?? 'organization',
      externalScopeId: body.externalScopeId ?? body.scopePath ?? 'generated-scope',
      scopePath: body.scopePath ?? body.externalScopeId ?? 'generated-scope',
      displayName: body.displayName ?? 'Generated Scope',
      verificationStatus: 'verified',
      isEnabled: body.isEnabled ?? true,
      lastVerifiedAt: new Date().toISOString(),
      lastVerificationError: null,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    }

    providerScopesByConnection[connectionId] = [scope, ...(providerScopesByConnection[connectionId] ?? [])]

    return HttpResponse.json(scope, { status: 201 })
  }),

  http.patch(`${base}/clients/:clientId/provider-connections/:connectionId/scopes/:scopeId`, async ({ params, request }) => {
    await delay(240)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const scopeId = String(params.scopeId)

    if (!getProviderConnection(clientId, connectionId)) {
      return new HttpResponse(null, { status: 404 })
    }

    const scopes = providerScopesByConnection[connectionId] ?? []
    const index = scopes.findIndex((scope) => scope.id === scopeId)

    if (index === -1) {
      return new HttpResponse(null, { status: 404 })
    }

    const body = await request.json() as any
    scopes[index] = {
      ...scopes[index],
      displayName: body.displayName ?? scopes[index].displayName,
      isEnabled: body.isEnabled ?? scopes[index].isEnabled,
      verificationStatus: body.verificationStatus ?? scopes[index].verificationStatus,
      lastVerificationError: body.lastVerificationError ?? scopes[index].lastVerificationError,
      updatedAt: new Date().toISOString(),
    }

    return HttpResponse.json(scopes[index])
  }),

  http.get(`${base}/clients/:clientId/provider-connections/:connectionId/reviewer-identities/resolve`, async ({ params, request }) => {
    await delay(220)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)

    if (!getProviderConnection(clientId, connectionId)) {
      return new HttpResponse(null, { status: 404 })
    }

    const url = new URL(request.url)
    const search = url.searchParams.get('search')?.trim().toLowerCase()
    const identities = providerReviewerIdentityCandidatesByConnection[connectionId] ?? []

    if (!search) {
      return HttpResponse.json(identities)
    }

    return HttpResponse.json(
      identities.filter((identity) =>
        identity.login.toLowerCase().includes(search) || identity.displayName.toLowerCase().includes(search)))
  }),

  http.get(`${base}/clients/:clientId/provider-connections/:connectionId/reviewer-identity`, async ({ params }) => {
    await delay(180)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)

    if (!getProviderConnection(clientId, connectionId)) {
      return new HttpResponse(null, { status: 404 })
    }

    const identity = providerReviewerIdentitiesByConnection[connectionId]
    if (!identity) {
      return new HttpResponse(null, { status: 404 })
    }

    return HttpResponse.json(identity)
  }),

  http.put(`${base}/clients/:clientId/provider-connections/:connectionId/reviewer-identity`, async ({ params, request }) => {
    await delay(240)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const connection = getProviderConnection(clientId, connectionId)

    if (!connection) {
      return new HttpResponse(null, { status: 404 })
    }

    const body = await request.json() as any
    const selectedIdentity =
      (providerReviewerIdentityCandidatesByConnection[connectionId] ?? []).find((identity) =>
        identity.externalUserId === body.externalUserId || identity.id === body.id) ?? {
        id: `provider-reviewer-${Math.random().toString(36).slice(2, 10)}`,
        clientId,
        connectionId,
        providerFamily: connection.providerFamily,
        externalUserId: body.externalUserId ?? 'provider-reviewer',
        login: body.login ?? 'provider-reviewer',
        displayName: body.displayName ?? 'Provider Reviewer',
        isBot: body.isBot ?? true,
      }

    providerReviewerIdentitiesByConnection[connectionId] = {
      ...selectedIdentity,
      updatedAt: new Date().toISOString(),
    }

    return HttpResponse.json(providerReviewerIdentitiesByConnection[connectionId])
  }),

  http.delete(`${base}/clients/:clientId/provider-connections/:connectionId/reviewer-identity`, async ({ params }) => {
    await delay(180)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)

    if (!getProviderConnection(clientId, connectionId)) {
      return new HttpResponse(null, { status: 404 })
    }

    providerReviewerIdentitiesByConnection[connectionId] = null
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/admin/users`, async () => {
    await delay(400)
    return HttpResponse.json([
      { id: 'u1', username: 'admin', globalRole: 'Admin', isActive: true, createdAt: new Date().toISOString() },
      { id: 'u2', username: 'jsmith', globalRole: 'User', isActive: true, createdAt: new Date().toISOString() },
      { id: 'u3', username: 'former.employee', globalRole: 'User', isActive: false, createdAt: new Date().toISOString() }
    ])
  }),

  http.get(`${base}/admin/users/:id`, async () => {
    return HttpResponse.json({
       assignments: [
         { assignmentId: 'a1', clientId: '1', role: 'ClientAdministrator', assignedAt: new Date().toISOString() },
         { assignmentId: 'a2', clientId: '2', role: 'ClientUser', assignedAt: new Date().toISOString() }
       ]
    })
  }),

  http.patch(`${base}/admin/users/:id`, async () => {
    await delay(200)
    return new HttpResponse(null, { status: 204 })
  }),

  http.delete(`${base}/admin/users/:id/permanent`, async () => {
    await delay(200)
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/users/me/pats`, async () => {
    await delay(200)
    return HttpResponse.json([
      { id: 'p1', label: 'CI Pipeline', createdAt: new Date().toISOString(), lastUsedAt: new Date().toISOString(), expiresAt: null, isRevoked: false },
      { id: 'p2', label: 'Local Dev Proxy', createdAt: new Date().toISOString(), lastUsedAt: null, expiresAt: new Date(Date.now() + 86400000).toISOString(), isRevoked: false }
    ])
  }),

  http.post(`${base}/users/me/pats`, async () => {
    await delay(300)
    return HttpResponse.json({ token: 'mock-pat-' + Math.random().toString(36).substring(7) })
  }),

  http.post(`${base}/users/me/password`, async () => {
    await delay(250)
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/jobs`, async () => {
    await delay(400)
    jobTick++

    const isCompleted = false

    return HttpResponse.json({
      items: [
        {
          id: 'job-123',
          providerProjectKey: 'proj-x',
          repositoryId: 'backend-service',
          pullRequestId: 42,
          providerScopePath: 'https://dev.azure.com/acme',
          status: 'completed',
          iterationId: 2,
          submittedAt: new Date(Date.now() - 86400000).toISOString(),
          processingStartedAt: new Date(Date.now() - 86000000).toISOString(),
          completedAt: new Date(Date.now() - 85000000).toISOString(),
          totalInputTokens: 12000,
          totalOutputTokens: 850,
          totalEstimatedCostUsd: 0.0426,
          costIsApproximate: false,
          resultSummary: 'Found 3 minor issues. Suggested improvements.',
          prTitle: 'feat: Add authentication middleware',
          prRepositoryName: 'backend-service',
          prSourceBranch: 'feature/auth-middleware',
          prTargetBranch: 'main',
          aiModel: 'claude-opus-4-5',
          clientId: '1'
        },
        {
          id: 'job-124',
          providerProjectKey: 'proj-y',
          repositoryId: 'frontend-app',
          pullRequestId: 89,
          providerScopePath: 'https://dev.azure.com/acme',
          status: isCompleted ? 'completed' : 'processing',
          iterationId: Math.ceil(jobTick / 2) || 1,
          submittedAt: new Date(Date.now() - 1000000).toISOString(),
          processingStartedAt: new Date(Date.now() - 500000).toISOString(),
          completedAt: isCompleted ? new Date().toISOString() : null,
          totalInputTokens: 5000 + (jobTick * 200),
          totalOutputTokens: jobTick * 100,
          resultSummary: isCompleted
            ? 'Automated review finished. LGTM!'
            : `Evaluating subjob ${jobTick}: src/components/Component${Math.ceil(jobTick/2)}.vue`,
          prTitle: 'refactor: Migrate to Composition API',
          prRepositoryName: 'frontend-app',
          prSourceBranch: 'refactor/composition-api',
          prTargetBranch: 'develop',
          aiModel: 'gpt-4o',
          clientId: '1'
        },
        {
          id: 'job-125',
          providerProjectKey: 'proj-z',
          repositoryId: 'infrastructure',
          pullRequestId: 12,
          providerScopePath: 'https://dev.azure.com/acme',
          status: 'failed',
          iterationId: 1,
          submittedAt: new Date(Date.now() - 200000000).toISOString(),
          processingStartedAt: new Date(Date.now() - 190000000).toISOString(),
          completedAt: new Date(Date.now() - 180000000).toISOString(),
          errorMessage: 'Failed to access ADO repository due to expired token.',
          prTitle: 'chore: Update Terraform modules',
          prRepositoryName: 'infrastructure',
          prSourceBranch: 'chore/terraform-update',
          prTargetBranch: 'main',
          aiModel: 'gemini-2.5-pro',
          clientId: '1'
        },
        {
          id: 'job-large',
          providerProjectKey: 'proj-w',
          repositoryId: 'large-monorepo',
          pullRequestId: 301,
          providerScopePath: 'https://dev.azure.com/acme',
          status: 'completed',
          iterationId: 1,
          submittedAt: new Date('2026-06-12T18:00:00Z').toISOString(),
          processingStartedAt: new Date('2026-06-12T18:00:05Z').toISOString(),
          completedAt: new Date('2026-06-12T19:00:00Z').toISOString(),
          totalInputTokens: 250000,
          totalOutputTokens: 18000,
          totalEstimatedCostUsd: 1.187,
          costIsApproximate: true,
          resultSummary: 'Large review: 25 files, 1000 events. Used for performance testing.',
          prTitle: 'feat: Full authentication and dashboard overhaul (25 files)',
          prRepositoryName: 'large-monorepo',
          prSourceBranch: 'feature/auth-dashboard-overhaul',
          prTargetBranch: 'main',
          aiModel: 'claude-sonnet-4-6',
          clientId: '1'
        }
      ]
    })
  }),

  http.get(`${base}/jobs/:id`, async ({ params }) => {
    await delay(250)

    const id = params.id as string

    if (id === 'job-124') {
      const isCompleted = false
      return HttpResponse.json({
        id,
        clientId: '1',
        status: isCompleted ? 2 : 1,
        submittedAt: new Date(Date.now() - 1000000).toISOString(),
        processingStartedAt: new Date(Date.now() - 500000).toISOString(),
        completedAt: isCompleted ? new Date().toISOString() : null,
        totalInputTokens: 5000 + (jobTick * 200),
        totalOutputTokens: jobTick * 100,
        errorMessage: null,
        aiModel: 'gpt-4o',
        reviewTemperature: 0.35,
        tokenBreakdown: [],
        breakdownConsistent: true,
      })
    }

    if (id === 'job-125') {
      return HttpResponse.json({
        id,
        clientId: '1',
        status: 3,
        submittedAt: new Date(Date.now() - 200000000).toISOString(),
        processingStartedAt: new Date(Date.now() - 190000000).toISOString(),
        completedAt: new Date(Date.now() - 180000000).toISOString(),
        totalInputTokens: 0,
        totalOutputTokens: 0,
        errorMessage: 'Failed to access ADO repository due to expired token.',
        aiModel: 'gemini-2.5-pro',
        reviewTemperature: 0.2,
        tokenBreakdown: [],
        breakdownConsistent: true,
      })
    }

    if (id === 'job-large') {
      return HttpResponse.json({
        id,
        clientId: '1',
        status: 2,
        submittedAt: new Date('2026-06-12T18:00:00Z').toISOString(),
        processingStartedAt: new Date('2026-06-12T18:00:05Z').toISOString(),
        completedAt: new Date('2026-06-12T19:00:00Z').toISOString(),
        totalInputTokens: 250000,
        totalOutputTokens: 18000,
        errorMessage: null,
        aiModel: 'claude-sonnet-4-6',
        reviewTemperature: 0.35,
        tokenBreakdown: [],
        breakdownConsistent: true,
      })
    }

    return HttpResponse.json({
      id,
      clientId: '1',
      status: 2,
      submittedAt: new Date(Date.now() - 86400000).toISOString(),
      processingStartedAt: new Date(Date.now() - 86000000).toISOString(),
      completedAt: new Date(Date.now() - 85000000).toISOString(),
      totalInputTokens: 12000,
      totalOutputTokens: 850,
      errorMessage: null,
      aiModel: 'claude-opus-4-5',
      reviewTemperature: 0.35,
      tokenBreakdown: [],
      breakdownConsistent: true,
    })
  }),

  http.get(`${base}/Reviews/:jobId`, async ({ params }) => {
    await delay(300)

    const jobId = params.jobId as string

    // Simulating "No Synthesis" for a failed job
    if (jobId === 'job-125') {
        return HttpResponse.json({
            jobId,
            status: 'failed',
            result: null
        })
    }

    // Simulating "In Progress" for a processing job
    if (jobId === 'job-124') {
        const isSynthesizing = jobTick > 8 // Lets say it synthesizes after 4 file reviews
        return HttpResponse.json({
            jobId,
            status: 'processing',
            result: isSynthesizing ? {
                summary: "Partial summary: The review is ongoing...",
                comments: []
            } : null
        })
    }

    // Provide a mocked synthesis review result for others (completed)
    return HttpResponse.json({
        jobId,
        status: 'completed',
        result: {
            summary: "**AI Review Summary**\n\nThe PR delivers a comprehensive Azure deployment example with supporting documentation, diagrams, and Bicep modules, but a few implementation issues need addressing before it can be considered ready. The README is thorough and matches the template wiring, and the new deployment diagram and Dockerfiles are largely informational. The PowerShell deployment script is well organized but could be tightened around secret handling and credentials exposure.\n\nIn the infrastructure modules, the main Bicep file has a resource-naming bug derived from `projectName` that can break deployments; containerApps.bicep omits the `shareName` for the AzureFile volume (so the mount cannot succeed) and also has opportunities to harden ingress/security settings and avoid hardcoded IDs. Overall the architecture is well laid out but tightening these areas will improve security, reliability, and usability.",
            comments: [
                {
                    filePath: "/.azure/modules/network.bicep",
                    lineNumber: 7,
                    severity: "suggestion",
                    message: "Consider parameterizing the address space prefixes instead of hardcoding '10.0.0.0/16' to improve reuse and flexibility."
                },
                {
                    filePath: "/.azure/modules/containerApps.bicep",
                    lineNumber: 101,
                    severity: "error",
                    message: "Role assignment depends on `db.identity.principalId` but has no explicit `dependsOn` on `db`. Managed identity service principals can be eventually consistent; this can cause intermittent `PrincipalNotFound` during deployment. Add `dependsOn: [db]` (or a deterministic dependency path) to ensure identity exists before assignment."
                },
                {
                    filePath: "/.azure/modules/containerEnvironment.bicep",
                    lineNumber: 10,
                    severity: "warning",
                    message: "Using `Microsoft.App/managedEnvironments@2025-10-02-preview` introduces preview API risk (breaking changes/region support drift). For production IaC, prefer the latest stable API version unless a required feature is preview-only."
                }
            ]
        }
    })
  }),

  // Admin-authenticated result endpoint (used by management UI instead of /Reviews/:jobId)
  http.get(`${base}/jobs/:id/result`, async ({ params }) => {
    await delay(300)

    const id = params.id as string

    if (id === 'job-125') {
      return new HttpResponse(null, { status: 404 })
    }

    if (id === 'job-124') {
      const isSynthesizing = jobTick > 8
      if (!isSynthesizing) return new HttpResponse(null, { status: 404 })
      return HttpResponse.json({
        jobId: id,
        status: 'processing',
        submittedAt: new Date(Date.now() - 1000000).toISOString(),
        completedAt: null,
        result: {
          summary: "Partial summary: The review is ongoing...",
          comments: []
        }
      })
    }

    if (id === 'job-large') {
      return HttpResponse.json({
        jobId: id,
        status: 'completed',
        submittedAt: new Date('2026-06-12T18:00:00Z').toISOString(),
        completedAt: new Date('2026-06-12T19:00:00Z').toISOString(),
        result: {
          summary: "**Large Review Summary (Performance Test)**\n\n25 files reviewed across frontend components, stores, services, composables, and backend controllers. The authentication overhaul is well-structured but several files have security concerns around token handling. Dashboard components have minor performance issues. Overall the PR is approvable with the noted fixes.",
          comments: [
            { filePath: 'src/services/authService.ts', lineNumber: 42, severity: 'error', message: 'Tokens are stored in localStorage instead of an httpOnly cookie, making them vulnerable to XSS.' },
            { filePath: 'src/composables/useAuth.ts', lineNumber: 18, severity: 'warning', message: 'No token refresh logic — sessions will expire silently.' },
            { filePath: 'backend/api/controllers/AuthController.cs', lineNumber: 87, severity: 'suggestion', message: 'Consider adding rate limiting to the login endpoint.' },
          ]
        }
      })
    }

    return HttpResponse.json({
      jobId: id,
      status: 'completed',
      submittedAt: new Date(Date.now() - 86400000).toISOString(),
      completedAt: new Date(Date.now() - 85000000).toISOString(),
      result: {
        summary: "**AI Review Summary**\n\nThe PR delivers a comprehensive Azure deployment example with supporting documentation, diagrams, and Bicep modules, but a few implementation issues need addressing before it can be considered ready. The README is thorough and matches the template wiring, and the new deployment diagram and Dockerfiles are largely informational. The PowerShell deployment script is well organized but could be tightened around secret handling and credentials exposure.\n\nIn the infrastructure modules, the main Bicep file has a resource-naming bug derived from `projectName` that can break deployments; containerApps.bicep omits the `shareName` for the AzureFile volume (so the mount cannot succeed) and also has opportunities to harden ingress/security settings and avoid hardcoded IDs. Overall the architecture is well laid out but tightening these areas will improve security, reliability, and usability.",
        comments: [
          {
            filePath: "/.azure/modules/network.bicep",
            lineNumber: 7,
            severity: "suggestion",
            message: "Consider parameterizing the address space prefixes instead of hardcoding '10.0.0.0/16' to improve reuse and flexibility."
          },
          {
            filePath: "/.azure/modules/containerApps.bicep",
            lineNumber: 101,
            severity: "error",
            message: "Role assignment depends on `db.identity.principalId` but has no explicit `dependsOn` on `db`. Managed identity service principals can be eventually consistent; this can cause intermittent `PrincipalNotFound` during deployment. Add `dependsOn: [db]` (or a deterministic dependency path) to ensure identity exists before assignment."
          },
          {
            filePath: "/.azure/modules/containerEnvironment.bicep",
            lineNumber: 10,
            severity: "warning",
            message: "Using `Microsoft.App/managedEnvironments@2025-10-02-preview` introduces preview API risk (breaking changes/region support drift). For production IaC, prefer the latest stable API version unless a required feature is preview-only."
          }
        ]
      }
    })
  }),

  http.get(`${base}/jobs/:id/protocol`, async ({ params }) => {
    await delay(600)

    if (params.id === 'job-124') {
        const events = []
        const currentTick = Math.min(jobTick, 8)

        for (let i = 1; i <= currentTick; i++) {
            // Odd ticks generate a fresh ToolCall
            events.push({
                id: `e${i}_call`,
                occurredAt: new Date(Date.now() - 500000 + i * 1500).toISOString(),
                kind: 'ToolCall',
                name: 'AnalyzeCodeChunk',
                inputTokens: 500, outputTokens: 0,
                inputTextSample: `function execute() {\n  return "processing chunk ${Math.ceil(i/2)}";\n}`,
                outputSummary: null
            })
            // Even ticks "answer" the previous call with a ToolResult
            if (i % 2 === 0) {
                events.push({
                    id: `e${i}_result`,
                    occurredAt: new Date(Date.now() - 500000 + i * 1500 + 800).toISOString(),
                    kind: 'ToolResult',
                    name: 'AnalyzeCodeChunk',
                    inputTokens: 0, outputTokens: 100,
                    inputTextSample: null,
                    outputSummary: `Analysis complete. Chunk ${i/2} is clean and optimal.`
                })
            }
        }

        const isCompleted = false
        return HttpResponse.json([
          {
            id: 'pass124',
            jobId: 'job-124',
            label: `src/components/Component${Math.ceil(currentTick/2)}.vue`,
            startedAt: new Date(Date.now() - 500000).toISOString(),
            completedAt: isCompleted ? new Date().toISOString() : null,
            outcome: isCompleted ? 'Success' : 'Processing',
            iterationCount: Math.ceil(currentTick / 2),
            toolCallCount: Math.floor(currentTick / 2),
            finalConfidence: isCompleted ? 99 : null,
            totalInputTokens: 5000 + (currentTick * 200),
            totalOutputTokens: currentTick * 100,
            events
          }
        ])
    }

    if (params.id === 'job-large') {
        // Return passes without events (events loaded per-pass via the detail endpoint)
        const passes = generateLargeReviewProtocols().map(({ events: _events, ...pass }) => ({ ...pass, events: [] }))
        return HttpResponse.json(passes)
    }

    return HttpResponse.json(protocolMockData)
  }),

  // Per-protocol detail endpoint (returns a single pass with full events)
  http.get(`${base}/jobs/:id/protocol/:protocolId`, async ({ params }) => {
    await delay(400)

    if (params.id === 'job-large') {
        const all = generateLargeReviewProtocols()
        const pass = all.find(p => p.id === params.protocolId)
        if (!pass) return new HttpResponse(null, { status: 404 })
        return HttpResponse.json(pass)
    }

    return new HttpResponse(null, { status: 404 })
  }),

  // Crawl Configurations
  http.get(`${base}/admin/crawl-configurations`, async () => {
    await delay(400)
    return HttpResponse.json(crawlConfigs)
  }),

  http.post(`${base}/admin/crawl-configurations`, async ({ request }) => {
    await delay(600)
    const body = await request.json() as any
    const scope = getScope(String(body.clientId), body.organizationScopeId)

    if (body.organizationScopeId && (!scope || scope.isEnabled === false)) {
      return HttpResponse.json({ error: 'The selected Azure DevOps organization is no longer available for this client.' }, { status: 409 })
    }

    const availableFilters = getCrawlFilters(body.organizationScopeId, body.providerProjectKey)
    const staleFilter = (body.repoFilters ?? []).find((filter: any) => {
      if (!filter?.canonicalSourceRef?.provider || !filter?.canonicalSourceRef?.value) {
        return false
      }

      return !availableFilters.some((option: any) =>
        option.canonicalSourceRef?.provider === filter.canonicalSourceRef.provider &&
        option.canonicalSourceRef?.value === filter.canonicalSourceRef.value,
      )
    })

    if (staleFilter) {
      return HttpResponse.json({ error: 'The selected crawl filter is no longer available in Azure DevOps.' }, { status: 409 })
    }

    const newConfig = {
      id: `config-${Math.random().toString(36).substr(2, 9)}`,
      clientId: body.clientId,
      organizationScopeId: body.organizationScopeId ?? null,
      providerScopePath: scope?.organizationUrl ?? body.providerScopePath,
      providerProjectKey: body.providerProjectKey,
      crawlIntervalSeconds: body.crawlIntervalSeconds ?? 60,
      isActive: true,
      repoFilters: body.repoFilters ?? [],
      proCursorSourceScopeMode: body.proCursorSourceScopeMode ?? 'allClientSources',
      proCursorSourceIds: body.proCursorSourceIds ?? [],
      invalidProCursorSourceIds: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    }
    crawlConfigs.unshift(newConfig)
    return HttpResponse.json(newConfig, { status: 201 })
  }),

  http.patch(`${base}/admin/crawl-configurations/:configId`, async ({ params, request }) => {
    await delay(500)
    const { configId } = params
    const body = await request.json() as any
    const idx = crawlConfigs.findIndex(c => c.id === configId)
    if (idx === -1) return new HttpResponse(null, { status: 404 })

    const existingConfig = crawlConfigs[idx]
    const availableFilters = getCrawlFilters(existingConfig.organizationScopeId, existingConfig.providerProjectKey)
    const staleFilter = (body.repoFilters ?? []).find((filter: any) => {
      if (!filter?.canonicalSourceRef?.provider || !filter?.canonicalSourceRef?.value) {
        return false
      }

      return !availableFilters.some((option: any) =>
        option.canonicalSourceRef?.provider === filter.canonicalSourceRef.provider &&
        option.canonicalSourceRef?.value === filter.canonicalSourceRef.value,
      )
    })

    if (staleFilter) {
      return HttpResponse.json({ error: 'The selected crawl filter is no longer available in Azure DevOps.' }, { status: 409 })
    }

    crawlConfigs[idx] = {
      ...existingConfig,
      ...body,
      repoFilters: body.repoFilters ?? existingConfig.repoFilters,
      proCursorSourceScopeMode: body.proCursorSourceScopeMode ?? existingConfig.proCursorSourceScopeMode,
      proCursorSourceIds: body.proCursorSourceIds ?? existingConfig.proCursorSourceIds,
      updatedAt: new Date().toISOString()
    }
    return HttpResponse.json(crawlConfigs[idx])
  }),

  http.delete(`${base}/admin/crawl-configurations/:configId`, async ({ params }) => {
    await delay(400)
    const { configId } = params
    const idx = crawlConfigs.findIndex(c => c.id === configId)
    if (idx === -1) return new HttpResponse(null, { status: 404 })

    crawlConfigs.splice(idx, 1)
    return new HttpResponse(null, { status: 204 })
  }),

  // Webhook Configurations
  http.get(`${base}/admin/webhook-configurations`, async () => {
    await delay(300)
    return HttpResponse.json(webhookConfigs.filter((config) => isProviderEnabled(config.provider)))
  }),

  http.get(`${base}/admin/webhook-configurations/:configId/deliveries`, async ({ params }) => {
    await delay(250)
    const configId = String(params.configId)
    return HttpResponse.json({
      items: webhookDeliveryLogsByConfig[configId] ?? [],
    })
  }),

  http.post(`${base}/admin/webhook-configurations`, async ({ request }) => {
    await delay(500)
    const body = await request.json() as any
    const scope = getScope(String(body.clientId), body.organizationScopeId)
    let providerSegment: string
    switch (body.provider) {
      case 'azureDevOps':
        providerSegment = 'ado'
        break
      case 'gitLab':
        providerSegment = 'gitlab'
        break
      case 'forgejo':
        providerSegment = 'forgejo'
        break
      default:
        providerSegment = 'github'
    }

    if (!Array.isArray(body.enabledEvents) || body.enabledEvents.length === 0) {
      return HttpResponse.json({ error: 'At least one enabled event is required.' }, { status: 400 })
    }

    if (!isProviderEnabled(body.provider)) {
      return HttpResponse.json({ error: 'The selected provider family is currently disabled by system administration.' }, { status: 409 })
    }

    if (body.organizationScopeId && (!scope || scope.isEnabled === false)) {
      return HttpResponse.json({ error: 'The selected Azure DevOps organization is no longer available for this client.' }, { status: 409 })
    }

    const availableFilters = getCrawlFilters(body.organizationScopeId, body.providerProjectKey)
    const staleFilter = (body.repoFilters ?? []).find((filter: any) => {
      if (!filter?.canonicalSourceRef?.provider || !filter?.canonicalSourceRef?.value) {
        return false
      }

      return !availableFilters.some((option: any) =>
        option.canonicalSourceRef?.provider === filter.canonicalSourceRef.provider &&
        option.canonicalSourceRef?.value === filter.canonicalSourceRef.value,
      )
    })

    if (staleFilter) {
      return HttpResponse.json({ error: 'The selected webhook filter is no longer available in Azure DevOps.' }, { status: 409 })
    }

    const created = {
      id: `webhook-config-${Math.random().toString(36).slice(2, 9)}`,
      clientId: body.clientId,
      provider: body.provider ?? 'azureDevOps',
      organizationScopeId: body.organizationScopeId ?? null,
      providerScopePath: scope?.organizationUrl ?? body.providerScopePath,
      providerProjectKey: body.providerProjectKey,
      isActive: true,
      enabledEvents: body.enabledEvents ?? [],
      repoFilters: (body.repoFilters ?? []).map((filter: any, index: number) => ({
        id: `webhook-filter-${index + 1}`,
        repositoryName: filter.repositoryName ?? filter.displayName ?? filter.canonicalSourceRef?.value,
        displayName: filter.displayName ?? filter.repositoryName ?? filter.canonicalSourceRef?.value,
        canonicalSourceRef: filter.canonicalSourceRef ?? null,
        targetBranchPatterns: filter.targetBranchPatterns ?? [],
      })),
      listenerUrl: `https://propr.example.com/webhooks/v1/providers/${providerSegment}/${Math.random().toString(16).slice(2, 18)}`,
      generatedSecret: 'generated-secret',
      createdAt: new Date().toISOString(),
    }

    webhookConfigs.unshift(created)
    webhookDeliveryLogsByConfig[created.id] = []
    return HttpResponse.json(created, { status: 201 })
  }),

  http.patch(`${base}/admin/webhook-configurations/:configId`, async ({ params, request }) => {
    await delay(450)
    const configId = String(params.configId)
    const idx = webhookConfigs.findIndex(config => config.id === configId)
    if (idx === -1) {
      return new HttpResponse(null, { status: 404 })
    }

    const body = await request.json() as any
    const existingConfig = webhookConfigs[idx]
    if (body.enabledEvents !== undefined && (!Array.isArray(body.enabledEvents) || body.enabledEvents.length === 0)) {
      return HttpResponse.json({ error: 'At least one enabled event is required.' }, { status: 400 })
    }

    const availableFilters = getCrawlFilters(existingConfig.organizationScopeId, existingConfig.providerProjectKey)
    const staleFilter = (body.repoFilters ?? []).find((filter: any) => {
      if (!filter?.canonicalSourceRef?.provider || !filter?.canonicalSourceRef?.value) {
        return false
      }

      return !availableFilters.some((option: any) =>
        option.canonicalSourceRef?.provider === filter.canonicalSourceRef.provider &&
        option.canonicalSourceRef?.value === filter.canonicalSourceRef.value,
      )
    })

    if (staleFilter) {
      return HttpResponse.json({ error: 'The selected webhook filter is no longer available in Azure DevOps.' }, { status: 409 })
    }

    webhookConfigs[idx] = {
      ...existingConfig,
      ...body,
      repoFilters: body.repoFilters ?? existingConfig.repoFilters,
      generatedSecret: null,
    }

    return HttpResponse.json(webhookConfigs[idx])
  }),

  http.delete(`${base}/admin/webhook-configurations/:configId`, async ({ params }) => {
    await delay(300)
    const configId = String(params.configId)
    const idx = webhookConfigs.findIndex(config => config.id === configId)
    if (idx === -1) {
      return new HttpResponse(null, { status: 404 })
    }

    webhookConfigs.splice(idx, 1)
    delete webhookDeliveryLogsByConfig[configId]
    return new HttpResponse(null, { status: 204 })
  }),

  // Dismissals
  http.get(`${base}/clients/:clientId/dismissals`, async () => {
    await delay(300)
    return HttpResponse.json(dismissedFindings)
  }),

  http.post(`${base}/clients/:clientId/dismissals`, async ({ request }) => {
    await delay(500)
    const body = await request.json() as any
    const newItem = {
      id: `d-${Math.random().toString(36).substr(2, 9)}`,
      clientId: '1',
      patternText: body.originalMessage,
      label: body.label,
      createdAt: new Date().toISOString()
    }
    dismissedFindings.unshift(newItem)
    return HttpResponse.json(newItem, { status: 201 })
  }),

  http.delete(`${base}/clients/:clientId/dismissals/:id`, async ({ params }) => {
    await delay(300)
    const { id } = params
    dismissedFindings = dismissedFindings.filter(d => d.id !== id)
    return new HttpResponse(null, { status: 204 })
  }),

  // Prompt Overrides
  http.get(`${base}/clients/:clientId/prompt-overrides`, async () => {
    await delay(300)
    return HttpResponse.json(promptOverrides)
  }),

  http.post(`${base}/clients/:clientId/prompt-overrides`, async ({ request }) => {
    await delay(500)
    const body = await request.json() as any
    const newItem = {
      id: `o-${Math.random().toString(36).substr(2, 9)}`,
      clientId: '1',
      scope: body.scope,
      crawlConfigId: body.crawlConfigId,
      promptKey: body.promptKey,
      overrideText: body.overrideText,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    }
    promptOverrides.unshift(newItem)
    return HttpResponse.json(newItem, { status: 201 })
  }),

  http.delete(`${base}/clients/:clientId/prompt-overrides/:id`, async ({ params }) => {
    await delay(300)
    const { id } = params
    promptOverrides = promptOverrides.filter(o => o.id !== id)
    return new HttpResponse(null, { status: 204 })
  }),

  // Thread Memory
  http.get(`${base}/admin/thread-memory`, async ({ request }) => {
    await delay(400)
    const url = new URL(request.url)
    const clientId = url.searchParams.get('clientId')
    const search = url.searchParams.get('search')?.toLowerCase()
    const page = Number(url.searchParams.get('page') || '1')
    const pageSize = Number(url.searchParams.get('pageSize') || '50')

    let items = threadMemoryRecords.filter(r => r.clientId === clientId)
    if (search) {
      items = items.filter(r =>
        r.repositoryId.toLowerCase().includes(search) ||
        r.filePath?.toLowerCase().includes(search) ||
        r.resolutionSummary.toLowerCase().includes(search)
      )
    }

    const totalCount = items.length
    const paginatedItems = items.slice((page - 1) * pageSize, page * pageSize)

    return HttpResponse.json({
      items: paginatedItems,
      totalCount,
      page,
      pageSize
    })
  }),

  http.delete(`${base}/admin/thread-memory/:id`, async ({ params }) => {
    await delay(300)
    const { id } = params
    threadMemoryRecords = threadMemoryRecords.filter(r => r.id !== id)
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/admin/thread-memory/activity-log`, async ({ request }) => {
    await delay(500)
    const url = new URL(request.url)
    const clientId = url.searchParams.get('clientId')
    const action = url.searchParams.get('action')
    const page = Number(url.searchParams.get('page') || '1')
    const pageSize = Number(url.searchParams.get('pageSize') || '50')

    let items = memoryActivityLog.filter(l => l.clientId === clientId)
    if (action != null && action !== '') {
      items = items.filter(l => l.action === Number(action))
    }

    const totalCount = items.length
    const paginatedItems = items.slice((page - 1) * pageSize, page * pageSize)

    return HttpResponse.json({
      items: paginatedItems,
      totalCount,
      page,
      pageSize
    })
  }),

  // Client Token Usage
  http.get(`${base}/admin/clients/:clientId/token-usage`, async ({ params }) => {
    const clientId = params.clientId as string
    const today = new Date()
    const samples = Array.from({ length: 14 }, (_, i) => {
      const d = new Date(today)
      d.setDate(d.getDate() - (13 - i))
      const date = d.toISOString().slice(0, 10)
      return [
        { connectionCategory: 1, modelId: 'gpt-5.4-mini', date, inputTokens: 1200 + i * 80, outputTokens: 300 + i * 20 },
        { connectionCategory: 5, modelId: 'gpt-5.4-nano', date, inputTokens: 600 + i * 40, outputTokens: 150 + i * 10 },
      ]
    }).flat()
    const totalInputTokens = samples.reduce((sum, s) => sum + s.inputTokens, 0)
    const totalOutputTokens = samples.reduce((sum, s) => sum + s.outputTokens, 0)
    return HttpResponse.json({
      clientId,
      from: samples[0].date,
      to: samples[samples.length - 1].date,
      totalInputTokens,
      totalOutputTokens,
      samples,
    })
  }),

  // Retained Pull Request Archive (read-only threads, files, and stored diffs)
  http.get(`${base}/clients/:clientId/review-archive/pull-requests/threads`, async ({ request }) => {
    await delay(180)
    const url = new URL(request.url)
    const repositoryId = url.searchParams.get('repositoryId')
    const pullRequestId = Number(url.searchParams.get('pullRequestId'))

    if (!hasRetainedArchive(repositoryId, pullRequestId)) {
      return HttpResponse.json([])
    }

    return HttpResponse.json(retainedThreads)
  }),

  http.get(`${base}/clients/:clientId/review-archive/pull-requests/files`, async ({ request }) => {
    await delay(180)
    const url = new URL(request.url)
    const repositoryId = url.searchParams.get('repositoryId')
    const pullRequestId = Number(url.searchParams.get('pullRequestId'))

    if (!hasRetainedArchive(repositoryId, pullRequestId)) {
      return HttpResponse.json([])
    }

    return HttpResponse.json(retainedFiles)
  }),

  http.get(`${base}/clients/:clientId/review-archive/pull-requests/file-diff`, async ({ request }) => {
    await delay(180)
    const url = new URL(request.url)
    const repositoryId = url.searchParams.get('repositoryId')
    const pullRequestId = Number(url.searchParams.get('pullRequestId'))
    const filePath = url.searchParams.get('filePath')

    if (!hasRetainedArchive(repositoryId, pullRequestId)) {
      return new HttpResponse(null, { status: 404 })
    }

    const diff = retainedFileDiffs.find((entry) => entry.filePath === filePath)
    if (!diff) {
      // The documented "no stored diff for this file" case.
      return new HttpResponse(null, { status: 404 })
    }

    return HttpResponse.json(diff)
  }),

  // PR Review View Aggregated Data
  http.get(`${base}/clients/:clientId/pr-view`, async ({ request, params }) => {
    await delay(500)
    const url = new URL(request.url)
    const providerScopePath = url.searchParams.get('providerScopePath')
    const providerProjectKey = url.searchParams.get('providerProjectKey')
    const repositoryId = url.searchParams.get('repositoryId')
    const pullRequestId = Number(url.searchParams.get('pullRequestId'))
    const clientId = params.clientId as string

    // Mock data for PR #81 (as seen in user screenshot) or default
    const isSpecialPR = pullRequestId === 81 || pullRequestId === 42

    return HttpResponse.json({
        clientId,
      providerScopePath: providerScopePath || 'https://dev.azure.com/meister-propr',
      providerProjectKey: providerProjectKey || 'Meister-ProPR',
        repositoryId: repositoryId || 'ai-dev-days-local-test',
        pullRequestId: pullRequestId || 81,
        totalJobs: isSpecialPR ? 1 : 0,
        totalInputTokens: isSpecialPR ? 51355 : 0,
        totalOutputTokens: isSpecialPR ? 4658 : 0,
        originatedMemoryCount: 0,
        contributedMemoryCount: isSpecialPR ? 2 : 0,
        breakdownConsistent: true,
        aggregatedTokenBreakdown: isSpecialPR ? [
          { connectionCategory: 1, modelId: 'gpt-5.4-mini', totalInputTokens: 28775, totalOutputTokens: 1616 },
          { connectionCategory: 5, modelId: 'gpt-5.4-nano', totalInputTokens: 21317, totalOutputTokens: 2373 },
          { connectionCategory: 4, modelId: 'gpt-5.4-nano', totalInputTokens: 1263, totalOutputTokens: 669 },
          { connectionCategory: 2, modelId: 'gpt-5.3-codex', totalInputTokens: 0, totalOutputTokens: 0 }
        ] : [],
        jobs: isSpecialPR ? [
            {
                jobId: pullRequestId === 42 ? 'job-123' : '72bc4447-4fa5-4dc2-b869-bb80e4e980a7',
                status: 'completed',
                submittedAt: new Date(Date.now() - 3600000).toISOString(),
            totalInputTokens: 51355,
            totalOutputTokens: 4658,
                tokenBreakdown: [
              { connectionCategory: 1, modelId: 'gpt-5.4-mini', totalInputTokens: 28775, totalOutputTokens: 1616 },
              { connectionCategory: 5, modelId: 'gpt-5.4-nano', totalInputTokens: 21317, totalOutputTokens: 2373 },
              { connectionCategory: 4, modelId: 'gpt-5.4-nano', totalInputTokens: 1263, totalOutputTokens: 669 },
              { connectionCategory: 2, modelId: 'gpt-5.3-codex', totalInputTokens: 0, totalOutputTokens: 0 }
                ]
            }
        ] : []
    })
  }),

  // Stop a running/queued review job (canonical + /jobs alias).
  http.post(`${base}/reviewing/jobs/:jobId/stop`, async ({ params }) => {
    await delay(300)
    return HttpResponse.json({ jobId: params.jobId as string, status: 'stopped' })
  }),

  http.post(`${base}/jobs/:jobId/stop`, async ({ params }) => {
    await delay(300)
    return HttpResponse.json({ jobId: params.jobId as string, status: 'stopped' })
  }),

  // Blocked pull requests (list / block / unblock).
  http.get(`${base}/clients/:clientId/reviewing/blocked-prs`, async ({ params }) => {
    await delay(200)
    const clientId = params.clientId as string
    return HttpResponse.json(mockBlockedPrsByClient[clientId] ?? [])
  }),

  http.post(`${base}/clients/:clientId/reviewing/blocked-prs`, async ({ params, request }) => {
    await delay(300)
    const clientId = params.clientId as string
    const body = await request.json() as any
    const list = mockBlockedPrsByClient[clientId] ?? (mockBlockedPrsByClient[clientId] = [])
    const alreadyBlocked = list.some((entry) =>
      entry.providerScopePath === body.providerScopePath &&
      entry.providerProjectKey === body.providerProjectKey &&
      entry.repositoryId === body.repositoryId &&
      entry.pullRequestId === body.pullRequestId,
    )
    if (!alreadyBlocked) {
      list.push({
        id: `block-${Math.random().toString(36).slice(2, 11)}`,
        clientId,
        providerScopePath: body.providerScopePath,
        providerProjectKey: body.providerProjectKey,
        repositoryId: body.repositoryId,
        pullRequestId: body.pullRequestId,
        blockedByUserId: '0000-1111-2222-3333',
        blockedAt: new Date().toISOString(),
        reason: body.reason ?? null,
      })
    }
    return new HttpResponse(null, { status: 200 })
  }),


  // --- Code Insights: two surfaces, two audiences ---
  // The mock data deliberately exercises the states worth looking at rather than the happy path only: a metric
  // above its sample floor and one below it, a harvested thread that counts toward recall and one that does not,
  // and findings both with and without an outcome.

  http.get(`${base}/code-quality/types-over-time`, async ({ request }) => {
    await delay(220)
    const url = new URL(request.url)
    const repository = url.searchParams.get('repositoryId')
    const pullRequestId = url.searchParams.get('pullRequestId')

    // One pull request's own mix, for the view embedded in a review: a handful of findings across its increments
    // rather than a codebase's month.
    if (pullRequestId) {
      const perPr: Record<string, number[]> = {
        'logic-error': [3, 1],
        'error-handling-observability': [2, 2],
        'data-validation': [1, 0],
      }
      const prBuckets = ['2026-07-20', '2026-07-23']
      const prPoints = Object.entries(perPr).flatMap(([key, counts]) =>
        counts.map((count, index) => ({ bucketStart: prBuckets[index], key, count })),
      )

      return HttpResponse.json({
        points: prPoints,
        totalFindings: prPoints.reduce((total, point) => total + point.count, 0),
        keys: Object.keys(perPr),
      })
    }

    // A quiet repository shows a thinner mix, so switching the picker visibly changes the chart.
    const buckets = ['2026-07-06', '2026-07-13', '2026-07-20', '2026-07-27']
    const mix: Record<string, number[]> = repository === 'quiet-service'
      ? { 'naming-clarity': [1, 0, 2, 1] }
      : {
          'logic-error': [6, 4, 7, 5],
          'error-handling-observability': [3, 5, 2, 4],
          'data-validation': [2, 2, 3, 1],
          security: [1, 0, 2, 1],
          concurrency: [0, 1, 0, 2],
        }

    const points = Object.entries(mix).flatMap(([key, counts]) =>
      counts.map((count, index) => ({ bucketStart: buckets[index], key, count })),
    )

    return HttpResponse.json({
      points,
      totalFindings: points.reduce((total, point) => total + point.count, 0),
      keys: Object.keys(mix),
    })
  }),

  http.get(`${base}/code-quality/concentration`, async ({ request }) => {
    await delay(180)
    const url = new URL(request.url)
    const grain = url.searchParams.get('grain') ?? 'repository'
    const pullRequestId = url.searchParams.get('pullRequestId')

    // Scoped to one pull request (the view embedded in a review) only that pull request's own rows exist.
    if (pullRequestId) {
      if (grain === 'file') {
        return HttpResponse.json([
          { clientId: '1', clientName: 'Acme Corp', repositoryId: 'payments-api', repositoryName: 'payments-api', pullRequestId: Number(pullRequestId), filePath: 'src/Payments/RefundProcessor.cs', count: 5 },
          { clientId: '1', clientName: 'Acme Corp', repositoryId: 'payments-api', repositoryName: 'payments-api', pullRequestId: Number(pullRequestId), filePath: 'src/Api/WebhookController.cs', count: 3 },
          { clientId: '1', clientName: 'Acme Corp', repositoryId: 'payments-api', repositoryName: 'payments-api', pullRequestId: Number(pullRequestId), filePath: '', count: 1 },
        ])
      }

      return HttpResponse.json([
        { clientId: '1', clientName: 'Acme Corp', repositoryId: 'payments-api', repositoryName: 'payments-api', pullRequestId: Number(pullRequestId), filePath: null, count: 9 },
      ])
    }

    // Provider repository identifiers are opaque (several providers use a bare number) so the mock carries the
    // display name separately, including one repository with no recorded name so the fallback stays visible.
    if (grain === 'repository') {
      return HttpResponse.json([
        { clientId: '1', clientName: 'Acme Corp', repositoryId: 'payments-api', repositoryName: 'payments-api', pullRequestId: null, filePath: null, count: 48 },
        { clientId: '1', clientName: 'Acme Corp', repositoryId: '4', repositoryName: 'checkout-web', pullRequestId: null, filePath: null, count: 21 },
        { clientId: '2', clientName: 'Globex', repositoryId: 'quiet-service', repositoryName: null, pullRequestId: null, filePath: null, count: 4 },
      ])
    }

    if (grain === 'file') {
      return HttpResponse.json([
        { clientId: '1', clientName: 'Acme Corp', repositoryId: 'payments-api', repositoryName: 'payments-api', pullRequestId: null, filePath: 'src/Payments/RefundProcessor.cs', count: 14 },
        { clientId: '1', clientName: 'Acme Corp', repositoryId: 'payments-api', pullRequestId: null, filePath: 'src/Payments/LedgerWriter.cs', count: 9 },
        { clientId: '1', clientName: 'Acme Corp', repositoryId: 'payments-api', pullRequestId: null, filePath: 'src/Api/WebhookController.cs', count: 6 },
        { clientId: '1', clientName: 'Acme Corp', repositoryId: 'payments-api', pullRequestId: null, filePath: '', count: 3 },
      ])
    }

    if (grain === 'pullRequest') {
      return HttpResponse.json([
        { clientId: '1', clientName: 'Acme Corp', repositoryId: 'payments-api', repositoryName: 'payments-api', pullRequestId: 4821, filePath: null, count: 11 },
        { clientId: '1', clientName: 'Acme Corp', repositoryId: 'payments-api', pullRequestId: 4790, filePath: null, count: 7 },
      ])
    }

    return HttpResponse.json([
      { clientId: '1', clientName: 'Acme Corp', repositoryId: null, pullRequestId: null, filePath: null, count: 69 },
      { clientId: '2', clientName: 'Globex', repositoryId: null, pullRequestId: null, filePath: null, count: 4 },
    ])
  }),

  http.get(`${base}/code-quality/repositories`, async () => {
    await delay(200)

    // The entry: where the findings are, ranked by volume. Two of these belong to different clients, which is what
    // the row's second line is for.
    const rows = [
      { clientId: '1', clientName: 'Acme Corp', repositoryId: 'payments-api', repositoryName: 'payments-api', findings: 48, pullRequests: 17, files: 22, averagePerPullRequest: 48 / 17, lastActivityOn: '2026-07-28' },
      { clientId: '1', clientName: 'Acme Corp', repositoryId: '4', repositoryName: 'checkout-web', findings: 21, pullRequests: 9, files: 12, averagePerPullRequest: 21 / 9, lastActivityOn: '2026-07-26' },
      { clientId: '2', clientName: 'Globex', repositoryId: 'quiet-service', repositoryName: null, findings: 4, pullRequests: 3, files: 3, averagePerPullRequest: 4 / 3, lastActivityOn: '2026-07-11' },
    ]
    const totalFindings = rows.reduce((total, row) => total + row.findings, 0)
    const pullRequests = rows.reduce((total, row) => total + row.pullRequests, 0)

    return HttpResponse.json({
      totalFindings,
      repositories: rows.length,
      pullRequests,
      averagePerPullRequest: totalFindings / pullRequests,
      rows,
    })
  }),

  http.get(`${base}/code-quality/hotspots`, async ({ request }) => {
    await delay(200)
    const url = new URL(request.url)
    const filesFrom = url.searchParams.get('filesFromPullRequestId')

    // Grouped by definition: the same findings, one level deeper, and the ones the syntax could not place are
    // reported as a count rather than ranked as a bucket.
    if (url.searchParams.get('groupBy') === 'symbol') {
      const symbols = [
        { filePath: 'src/Payments/RefundProcessor.cs', symbolName: 'Process', findings: 18, pullRequests: 8, averagePerPullRequest: 18 / 8 },
        { filePath: 'src/Payments/RefundProcessor.cs', symbolName: 'ValidateRefund', findings: 9, pullRequests: 5, averagePerPullRequest: 9 / 5 },
        { filePath: 'src/Payments/LedgerWriter.cs', symbolName: 'Write', findings: 14, pullRequests: 6, averagePerPullRequest: 14 / 6 },
        { filePath: 'src/Api/WebhookController.cs', symbolName: 'Post', findings: 6, pullRequests: 4, averagePerPullRequest: 1.5 },
      ]
      const placed = symbols.reduce((total, symbol) => total + symbol.findings, 0)

      return HttpResponse.json({
        totalFindings: placed,
        pullRequests: filesFrom ? 12 : 26,
        averagePerPullRequest: placed / (filesFrom ? 12 : 26),
        fileCount: symbols.length,
        files: symbols,
        unplacedFindings: 42,
      })
    }

    // A pull-request-scoped ask reports only that pull request's files, but with the history they carry, which is
    // the whole point: three findings here, thirty over the file's life.
    const files = filesFrom
      ? [
          { filePath: 'src/Payments/RefundProcessor.cs', findings: 31, pullRequests: 11, averagePerPullRequest: 31 / 11 },
          { filePath: 'src/Api/WebhookController.cs', findings: 9, pullRequests: 5, averagePerPullRequest: 9 / 5 },
          { filePath: '', findings: 2, pullRequests: 2, averagePerPullRequest: 1 },
        ]
      : [
          { filePath: 'src/Payments/RefundProcessor.cs', findings: 31, pullRequests: 11, averagePerPullRequest: 31 / 11 },
          { filePath: 'src/Payments/LedgerWriter.cs', findings: 22, pullRequests: 9, averagePerPullRequest: 22 / 9 },
          { filePath: 'src/Payments/Fees/FeeCalculator.cs', findings: 14, pullRequests: 6, averagePerPullRequest: 14 / 6 },
          { filePath: 'src/Api/WebhookController.cs', findings: 9, pullRequests: 5, averagePerPullRequest: 9 / 5 },
          { filePath: 'src/Api/HealthController.cs', findings: 3, pullRequests: 3, averagePerPullRequest: 1 },
          { filePath: 'tests/Payments/RefundProcessorTests.cs', findings: 6, pullRequests: 4, averagePerPullRequest: 1.5 },
          { filePath: '', findings: 4, pullRequests: 3, averagePerPullRequest: 4 / 3 },
        ]

    const totalFindings = files.reduce((total, file) => total + file.findings, 0)
    const pullRequests = filesFrom ? 12 : 26

    return HttpResponse.json({
      totalFindings,
      pullRequests,
      averagePerPullRequest: totalFindings / pullRequests,
      fileCount: files.length,
      files,
      unplacedFindings: 0,
    })
  }),

  http.get(`${base}/code-quality/survival`, async ({ request }) => {
    await delay(200)
    const url = new URL(request.url)
    const pullRequestId = url.searchParams.get('pullRequestId')

    // Scoped to one pull request, the totals and the single broken-out row are the same pull request.
    if (pullRequestId) {
      const own = {
        persisted: 6,
        fixed: 4,
        dropped: 2,
        total: 12,
        persistenceRate: 0.5,
        pullRequests: 1,
      }

      return HttpResponse.json({
        total: own,
        pullRequests: [
          {
            clientId: '1',
            repositoryId: 'payments-api',
            repositoryName: 'payments-api',
            pullRequestId: Number(pullRequestId),
            revisions: 3,
            survival: own,
          },
        ],
      })
    }

    // A quiet repository has nothing multi-increment in it, so the "nothing to say yet" state is reachable.
    if (url.searchParams.get('repositoryId') === 'quiet-service') {
      return HttpResponse.json({
        total: { persisted: 0, fixed: 0, dropped: 0, total: 0, persistenceRate: null, pullRequests: 0 },
        pullRequests: [],
      })
    }

    return HttpResponse.json({
      total: { persisted: 19, fixed: 11, dropped: 7, total: 37, persistenceRate: 19 / 37, pullRequests: 6 },
      pullRequests: [
        { clientId: '1', repositoryId: 'payments-api', repositoryName: 'payments-api', pullRequestId: 4790, revisions: 4, survival: { persisted: 2, fixed: 1, dropped: 4, total: 7, persistenceRate: 2 / 7, pullRequests: 1 } },
        { clientId: '1', repositoryId: 'payments-api', pullRequestId: 4821, revisions: 3, survival: { persisted: 6, fixed: 4, dropped: 2, total: 12, persistenceRate: 0.5, pullRequests: 1 } },
        { clientId: '1', repositoryId: '4', repositoryName: 'checkout-web', pullRequestId: 3312, revisions: 2, survival: { persisted: 5, fixed: 3, dropped: 1, total: 9, persistenceRate: 5 / 9, pullRequests: 1 } },
        { clientId: '1', repositoryId: 'payments-api', pullRequestId: 4802, revisions: 2, survival: { persisted: 6, fixed: 3, dropped: 0, total: 9, persistenceRate: 6 / 9, pullRequests: 1 } },
      ],
    })
  }),

  http.get(`${base}/code-quality/findings`, async ({ request }) => {
    await delay(200)
    const url = new URL(request.url)
    const coreType = url.searchParams.get('coreType')

    return HttpResponse.json(mockCodeInsightFindings(coreType))
  }),

  http.get(`${base}/reviewer-performance/quality`, async ({ request }) => {
    await delay(260)
    const url = new URL(request.url)
    // A repository narrowing lands on a scope with too few closed pull requests, so the suppressed state is
    // reachable in the mock rather than only in a unit test.
    const thin = url.searchParams.get('repositoryId') === 'quiet-service'

    const correctness = thin
      ? [
          { bucketStart: '2026-07-13', metric: mockMetric({ f1: 0.42, sampleSize: 1 }) },
          { bucketStart: '2026-07-20', metric: mockMetric({ f1: 0.90, sampleSize: 2 }) },
        ]
      : [
          // Eight weeks, because a trend is tested rather than read off the ends and the test needs that many.
          { bucketStart: '2026-06-01', metric: mockMetric({ precision: 0.66, recall: 0.47, f1: 0.55, sampleSize: 11 }) },
          { bucketStart: '2026-06-08', metric: mockMetric({ precision: 0.68, recall: 0.50, f1: 0.58, sampleSize: 13 }) },
          { bucketStart: '2026-06-15', metric: mockMetric({ precision: 0.70, recall: 0.52, f1: 0.60, sampleSize: 14 }) },
          { bucketStart: '2026-06-22', metric: mockMetric({ precision: 0.72, recall: 0.55, f1: 0.63, sampleSize: 17 }) },
          { bucketStart: '2026-06-29', metric: mockMetric({ precision: 0.75, recall: 0.57, f1: 0.65, sampleSize: 14 }) },
          { bucketStart: '2026-07-06', metric: mockMetric({ precision: 0.77, recall: 0.60, f1: 0.68, sampleSize: 17 }) },
          { bucketStart: '2026-07-13', metric: mockMetric({ precision: 0.79, recall: 0.61, f1: 0.69, sampleSize: 19 }) },
          { bucketStart: '2026-07-20', metric: mockMetric({ precision: 0.82, recall: 0.66, f1: 0.73, sampleSize: 22 }) },
        ]

    return HttpResponse.json({
      correctness,
      acceptance: [
        { bucketStart: '2026-06-01', metric: mockMetric({ acceptanceRate: 0.66, sampleSize: 62 }) },
        { bucketStart: '2026-06-08', metric: mockMetric({ acceptanceRate: 0.61, sampleSize: 70 }) },
        { bucketStart: '2026-06-15', metric: mockMetric({ acceptanceRate: 0.70, sampleSize: 66 }) },
        { bucketStart: '2026-06-22', metric: mockMetric({ acceptanceRate: 0.64, sampleSize: 81 }) },
        { bucketStart: '2026-06-29', metric: mockMetric({ acceptanceRate: 0.62, sampleSize: 84 }) },
        { bucketStart: '2026-07-06', metric: mockMetric({ acceptanceRate: 0.67, sampleSize: 91 }) },
        { bucketStart: '2026-07-13', metric: mockMetric({ acceptanceRate: 0.71, sampleSize: 78 }) },
        { bucketStart: '2026-07-20', metric: mockMetric({ acceptanceRate: 0.69, sampleSize: 40 }) },
      ],
      correctnessTotal: thin
        ? mockMetric({ precision: 0.66, recall: 0.5, f1: 0.57, sampleSize: 3 })
        : mockMetric({
            precision: 0.79,
            recall: 0.6,
            f1: 0.68,
            addressed: 132,
            acknowledged: 24,
            dismissed: 19,
            falsePositive: 46,
            misses: 117,
            sampleSize: 72,
          }),
      acceptanceTotal: mockMetric({
        acceptanceRate: 0.68,
        addressed: 132,
        acknowledged: 24,
        dismissed: 19,
        falsePositive: 46,
        sampleSize: 221,
        // Neither accepted nor rejected, and in neither ratio: roughly the share the published study found.
        discussed: 17,
      }),
      correctnessTrend: thin
        ? // Both of the thin buckets rest on fewer closed pull requests than the floor, so none was tested.
          { direction: 'insufficient', tau: null, pValue: null, slopePerPeriod: null, periods: 0 }
        : { direction: 'improving', tau: 1, pValue: 0.0002, slopePerPeriod: 0.026, periods: 8 },
      acceptanceTrend: { direction: 'flat', tau: 0.14, pValue: 0.71, slopePerPeriod: 0.004, periods: 8 },
      minimumSampleSize: 10,
      minimumTrendPeriods: 8,
    })
  }),

  http.get(`${base}/reviewer-performance/by-grain`, async ({ request }) => {
    await delay(200)
    const url = new URL(request.url)
    const grain = url.searchParams.get('grain') ?? 'repository'

    // Worst first, as the server ranks it, and one row deliberately below the sample floor so the suppressed
    // presentation is reachable here rather than only in a unit test.
    if (grain === 'client') {
      return HttpResponse.json([
        { clientId: '2', clientName: 'Globex', repositoryId: null, pullRequestId: null, metric: mockMetric({ precision: 0.55, recall: 0.4, f1: 0.46, falsePositive: 18, misses: 27, sampleSize: 21 }) },
        { clientId: '1', clientName: 'Acme Corp', repositoryId: null, pullRequestId: null, metric: mockMetric({ precision: 0.84, recall: 0.67, f1: 0.75, falsePositive: 28, misses: 90, sampleSize: 51 }) },
      ])
    }

    // Grouped by producing model: no client scope, no recall, no misses, and the sample counts resolved findings.
    // The last row is the unattributed tail: reviews that ran before the model was recorded.
    if (grain === 'model') {
      return HttpResponse.json([
        {
          clientId: null,
          clientName: null,
          repositoryId: null,
          pullRequestId: null,
          modelId: 'gpt-5.4-mini',
          logicalModelName: 'thrifty-reviewer',
          metric: mockMetric({ precision: 0.61, recall: null, f1: null, acceptanceRate: 0.52, addressed: 34, acknowledged: 9, dismissed: 12, falsePositive: 35, misses: 0, sampleSize: 90 }),
        },
        {
          clientId: null,
          clientName: null,
          repositoryId: null,
          pullRequestId: null,
          modelId: 'claude-opus-5',
          logicalModelName: 'balanced-reviewer',
          metric: mockMetric({ precision: 0.88, recall: null, f1: null, acceptanceRate: 0.74, addressed: 96, acknowledged: 15, dismissed: 21, falsePositive: 18, misses: 0, sampleSize: 150 }),
        },
        {
          clientId: null,
          clientName: null,
          repositoryId: null,
          pullRequestId: null,
          modelId: null,
          logicalModelName: null,
          metric: mockMetric({ precision: 0.8, recall: null, f1: null, acceptanceRate: 0.7, addressed: 5, acknowledged: 1, dismissed: 2, falsePositive: 2, misses: 0, sampleSize: 4 }),
        },
      ])
    }

    if (grain === 'pullRequest') {
      return HttpResponse.json([
        { clientId: '1', clientName: 'Acme Corp', repositoryId: 'payments-api', pullRequestId: 4790, metric: mockMetric({ precision: 0.5, recall: 0.33, f1: 0.4, falsePositive: 4, misses: 8, sampleSize: 12 }) },
        { clientId: '1', clientName: 'Acme Corp', repositoryId: 'payments-api', pullRequestId: 4821, metric: mockMetric({ precision: 0.86, recall: 0.7, f1: 0.77, falsePositive: 2, misses: 5, sampleSize: 14 }) },
        { clientId: '1', clientName: 'Acme Corp', repositoryId: 'checkout-web', pullRequestId: 3312, metric: mockMetric({ precision: 1, recall: 1, f1: 1, falsePositive: 0, misses: 0, sampleSize: 2 }) },
      ])
    }

    return HttpResponse.json([
      { clientId: '2', clientName: 'Globex', repositoryId: 'quiet-service', repositoryName: null, pullRequestId: null, metric: mockMetric({ precision: 0.5, recall: 0.31, f1: 0.38, falsePositive: 9, misses: 20, sampleSize: 11 }) },
      { clientId: '1', clientName: 'Acme Corp', repositoryId: '4', repositoryName: 'checkout-web', pullRequestId: null, metric: mockMetric({ precision: 0.72, recall: 0.58, f1: 0.64, falsePositive: 12, misses: 31, sampleSize: 18 }) },
      { clientId: '1', clientName: 'Acme Corp', repositoryId: 'payments-api', repositoryName: 'payments-api', pullRequestId: null, metric: mockMetric({ precision: 0.85, recall: 0.69, f1: 0.76, falsePositive: 21, misses: 62, sampleSize: 40 }) },
      { clientId: '1', clientName: 'Acme Corp', repositoryId: 'internal-tools', pullRequestId: null, metric: mockMetric({ precision: 1, recall: 1, f1: 1, falsePositive: 0, misses: 0, sampleSize: 3 }) },
    ])
  }),

  // Coverage of the collection against review history. Deliberately uneven across repositories: the reading a
  // reader has to be able to make here is "these numbers are thin because collection was off, not because the
  // reviewer found nothing".
  http.get(`${base}/reviewer-performance/rejection-reasons`, async () => {
    await delay(160)
    // Roughly the distribution the published study found: genuine mistakes are well under half of the
    // rejections, and the rest are spread over reasons that each call for a different fix.
    return HttpResponse.json({
      reasons: [
        { reason: 'Wrong', count: 26 },
        { reason: 'DesignTradeOff', count: 14 },
        { reason: 'DeveloperPreference', count: 11 },
        { reason: 'OutOfScope', count: 6 },
        { reason: 'Redundant', count: 3 },
      ],
      unclassified: 5,
      rejections: 65,
      // The two classes are turned down at similar rates for different reasons: the functional rejections are
      // mostly the reviewer being wrong, the evolvability ones mostly the team not wanting the advice.
      byConcernClass: [
        {
          concernClass: 'Functional',
          reasons: [
            { reason: 'Wrong', count: 21 },
            { reason: 'OutOfScope', count: 4 },
            { reason: 'Redundant', count: 2 },
            { reason: 'DesignTradeOff', count: 2 },
          ],
          unclassified: 3,
          rejections: 32,
        },
        {
          concernClass: 'Evolvability',
          reasons: [
            { reason: 'DeveloperPreference', count: 11 },
            { reason: 'DesignTradeOff', count: 12 },
            { reason: 'Wrong', count: 5 },
            { reason: 'OutOfScope', count: 2 },
            { reason: 'Redundant', count: 1 },
          ],
          unclassified: 2,
          rejections: 33,
        },
      ],
    })
  }),

  http.post(`${base}/reviewer-performance/import`, async ({ request }) => {
    await delay(400)
    const body = (await request.json()) as { includeOutcomes?: boolean }
    // Shaped like a real second run: some jobs were already collected, and some findings can never gain an
    // outcome because their comments were never linked to a thread.
    return HttpResponse.json({
      jobsRead: 42,
      jobsImported: 31,
      jobsAlreadyCollected: 11,
      findingsImported: 184,
      findingsWithoutThread: 26,
      pullRequests: 17,
      outcomeThreadsReplayed: body.includeOutcomes ? 58 : 0,
      humanThreadsReplayed: body.includeOutcomes ? 23 : 0,
      collectionDisabled: false,
      reachedLimit: false,
    })
  }),

  http.get(`${base}/reviewer-performance/coverage`, async () => {
    await delay(180)
    return HttpResponse.json({
      reviewJobs: 61,
      jobsCollected: 24,
      producedFindings: 508,
      collectedFindings: 173,
      pullRequests: 29,
      pullRequestsRetained: 11,
      clientsWithCollectionOff: 1,
      rows: [
        {
          clientId: '3',
          clientName: 'Umbrella Corp',
          repositoryId: 'legacy-billing',
          repositoryName: 'legacy-billing',
          reviewJobs: 18,
          jobsCollected: 0,
          producedFindings: 214,
          collectedFindings: 0,
          pullRequests: 9,
          pullRequestsRetained: 0,
          retainedThreads: 0,
          dispositions: 0,
          misses: 0,
          pullRequestsSealed: 0,
        },
        {
          clientId: '1',
          clientName: 'Acme Corp',
          repositoryId: 'checkout-web',
          repositoryName: 'checkout-web',
          reviewJobs: 21,
          jobsCollected: 8,
          producedFindings: 152,
          collectedFindings: 47,
          pullRequests: 11,
          pullRequestsRetained: 3,
          retainedThreads: 26,
          dispositions: 19,
          misses: 4,
          pullRequestsSealed: 2,
        },
        {
          clientId: '1',
          clientName: 'Acme Corp',
          repositoryId: 'payments-api',
          repositoryName: 'payments-api',
          reviewJobs: 22,
          jobsCollected: 16,
          producedFindings: 142,
          collectedFindings: 126,
          pullRequests: 9,
          pullRequestsRetained: 8,
          retainedThreads: 71,
          dispositions: 88,
          misses: 12,
          pullRequestsSealed: 6,
        },
      ],
    })
  }),

  http.get(`${base}/reviewer-performance/misses`, async () => {
    await delay(200)
    return HttpResponse.json([
      {
        id: 'miss-1',
        clientId: '1',
        repositoryId: 'payments-api',
        pullRequestId: 4821,
        providerThreadId: '90412',
        filePath: 'src/Payments/RefundProcessor.cs',
        lineNumber: 214,
        discussion: 'alice: this retries forever if the gateway returns 409: we need a ceiling\nbob: good catch, capped at 5',
        isSubstantive: true,
        wasActedOn: true,
        isInScope: true,
        countsAsMiss: true,
        classifierConfidence: 0.88,
        harvestedAt: '2026-07-22T09:14:00Z',
      },
      {
        id: 'miss-2',
        clientId: '1',
        repositoryId: 'payments-api',
        pullRequestId: 4790,
        providerThreadId: '90188',
        filePath: 'src/Api/WebhookController.cs',
        lineNumber: 47,
        discussion: 'carol: can we rename this to match the handler above?\ndave: sure',
        isSubstantive: false,
        wasActedOn: true,
        isInScope: false,
        countsAsMiss: false,
        classifierConfidence: 0.74,
        harvestedAt: '2026-07-21T15:02:00Z',
      },
      {
        id: 'miss-3',
        clientId: '1',
        repositoryId: 'checkout-web',
        pullRequestId: 3312,
        providerThreadId: '88740',
        filePath: 'src/checkout/session.ts',
        lineNumber: 88,
        discussion: 'erin: the session token is logged here in full',
        isSubstantive: true,
        wasActedOn: true,
        isInScope: true,
        countsAsMiss: true,
        classifierConfidence: 0.93,
        harvestedAt: '2026-07-20T11:40:00Z',
      },
    ])
  }),

  http.get(`${base}/reviewer-performance/findings`, async ({ request }) => {
    await delay(200)
    const url = new URL(request.url)
    const disposition = url.searchParams.get('disposition')

    return HttpResponse.json(
      mockCodeInsightFindings(null).filter((finding) =>
        !disposition || finding.disposition === disposition,
      ),
    )
  }),

  http.post(`${base}/clients/:clientId/reviewing/blocked-prs/unblock`, async ({ params, request }) => {
    await delay(300)
    const clientId = params.clientId as string
    const body = await request.json() as any
    const list = mockBlockedPrsByClient[clientId] ?? []
    mockBlockedPrsByClient[clientId] = list.filter((entry) =>
      !(entry.providerScopePath === body.providerScopePath &&
        entry.providerProjectKey === body.providerProjectKey &&
        entry.repositoryId === body.repositoryId &&
        entry.pullRequestId === body.pullRequestId),
    )
    return new HttpResponse(null, { status: 200 })
  })
]
