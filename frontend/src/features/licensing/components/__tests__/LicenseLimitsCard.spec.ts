// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { ref } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { authorOverageLabel } from '@/features/licensing/licensingCopy'
import type { LicenseLimit, LicensingSummary } from '@/services/licensingService'

const summary = ref<LicensingSummary | null>(null)

vi.mock('@/composables/useLicensing', () => ({
  useLicensing: () => ({ summary }),
}))

/** One limit, with the fields a test does not care about left at the no-license reading. */
function limit(overrides: Partial<LicenseLimit> & Pick<LicenseLimit, 'key'>): LicenseLimit {
  return {
    allowance: 'absent',
    licensedCount: null,
    informationalCount: null,
    effectiveCeiling: 'unlimited',
    effectiveCount: null,
    effectiveSource: 'community',
    excludedAutomationCount: null,
    ...overrides,
  }
}

function summaryWith(limits: LicenseLimit[], overrides: Partial<LicensingSummary> = {}): LicensingSummary {
  return {
    edition: 'commercial',
    activatedAt: '2026-01-02T09:15:00Z',
    capabilities: [],
    stage: 'active',
    notBefore: '2026-01-01T00:00:00Z',
    warningStartsAt: null,
    expiresAt: '2026-12-01T00:00:00Z',
    graceEndsAt: '2026-12-15T00:00:00Z',
    daysRemaining: 100,
    licensee: 'Contoso Engineering',
    licenseId: 'c9f5c9a2',
    limits,
    licensingIdentity: '5f2c0d1e',
    authorOverage: null,
    authorPeakMonth: null,
    ...overrides,
  }
}

/** The authors limit as a licensed installation reports it, for the tests about the author readings. */
function authorsLimit(): LicenseLimit {
  return limit({
    key: 'authorsPerMonth',
    allowance: 'count',
    licensedCount: 120,
    informationalCount: 11,
    effectiveCeiling: 'count',
    effectiveCount: 120,
    effectiveSource: 'license',
  })
}

/**
 * The month a peak reading names, read in UTC. The value on the wire is the first day of the month in UTC, so
 * this is what the card has to render wherever it runs.
 */
function peakMonthName(month: string): string {
  return new Date(month).toLocaleDateString(undefined, {
    month: 'long',
    year: 'numeric',
    timeZone: 'UTC',
  })
}

async function mountCard() {
  const { default: LicenseLimitsCard } =
    await import('@/features/licensing/components/LicenseLimitsCard.vue')

  const wrapper = mount(LicenseLimitsCard)
  mounted.push(wrapper)

  return wrapper
}

const originalTimeZone = process.env.TZ

const mounted: { unmount: () => void }[] = []

describe('LicenseLimitsCard', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    summary.value = summaryWith([])

    // Run behind UTC. The first day of a month read in local time falls in the month before it here, so a
    // reading that does not pin the zone renders the wrong month name and the peak tests catch it. A machine
    // set to UTC would let that pass.
    process.env.TZ = 'America/Los_Angeles'
  })

  afterEach(() => {
    mounted.splice(0).forEach((wrapper) => wrapper.unmount())

    if (originalTimeZone === undefined) {
      delete process.env.TZ
    } else {
      process.env.TZ = originalTimeZone
    }
  })

  // The ceiling in force is shown first, because a refusal quotes it. The three kinds of ceiling stay apart:
  // a number, a counted dimension held to no ceiling, and a dimension nothing counts.
  it('shows the ceiling the installation is held to first', async () => {
    summary.value = summaryWith([
      limit({ key: 'clients', allowance: 'count', licensedCount: 10, effectiveCeiling: 'count', effectiveCount: 10, effectiveSource: 'license' }),
      limit({ key: 'runners', allowance: 'unlimited', effectiveCeiling: 'unlimited', effectiveSource: 'license' }),
      limit({ key: 'authorsPerMonth', effectiveCeiling: 'unmetered' }),
    ])

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limit-effective-clients"]').text()).toBe('10')
    expect(wrapper.get('[data-testid="license-limit-effective-runners"]').text()).toBe('Unlimited')
    expect(wrapper.get('[data-testid="license-limit-effective-authorsPerMonth"]').text()).toBe('Not enforced')
  })

  it('says whether the ceiling comes from the license or from the Community limit', async () => {
    summary.value = summaryWith([
      limit({ key: 'clients', allowance: 'count', licensedCount: 10, effectiveCeiling: 'count', effectiveCount: 10, effectiveSource: 'license' }),
      limit({ key: 'runners', effectiveCeiling: 'count', effectiveCount: 0, effectiveSource: 'community' }),
    ])

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limit-source-clients"]').text()).toBe('From the license in force')
    expect(wrapper.get('[data-testid="license-limit-source-runners"]').text())
      .toBe('From the Community limit')
  })

  // The first divergence an operator has to be able to read: the license states nothing for the dimension,
  // and the ceiling enforced against it is the community value. Reporting only the stated side would leave a
  // refused enrollment unexplained.
  it('shows a limit the license leaves out beside the community ceiling in force', async () => {
    summary.value = summaryWith([
      limit({ key: 'runners', allowance: 'absent', effectiveCeiling: 'count', effectiveCount: 0, effectiveSource: 'community' }),
    ])

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limit-effective-runners"]').text()).toBe('0')
    expect(wrapper.get('[data-testid="license-limit-stated-runners"]').text())
      .toBe('The license does not state this limit')
    expect(wrapper.get('[data-testid="license-limit-source-runners"]').text())
      .toBe('From the Community limit')
  })

  // The second divergence: past the grace window the document still states its number, and the number in
  // force is the community value. Both are on the card, so the stated number is not read as the one enforced.
  it('shows the stated number beside the community ceiling once a license has reverted', async () => {
    summary.value = summaryWith([
      limit({ key: 'runners', allowance: 'count', licensedCount: 25, effectiveCeiling: 'count', effectiveCount: 0, effectiveSource: 'community' }),
    ])

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limit-effective-runners"]').text()).toBe('0')
    expect(wrapper.get('[data-testid="license-limit-stated-runners"]').text()).toBe('The license states 25')
  })

  it('reports a licensed limit with no ceiling as stating none', async () => {
    summary.value = summaryWith([
      limit({ key: 'clients', allowance: 'unlimited', effectiveCeiling: 'unlimited', effectiveSource: 'license' }),
    ])

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limit-stated-clients"]').text())
      .toBe('The license states no ceiling')
  })

  // The count is a reading taken when the page loads, so the copy says what the installation currently holds
  // rather than presenting the number as live. The subtitle names which dimensions a refusal can come from,
  // because three of the four are enforced.
  it('reports the current count beside the ceiling in force', async () => {
    summary.value = summaryWith([
      limit({ key: 'clients', allowance: 'count', licensedCount: 10, informationalCount: 4, effectiveCeiling: 'count', effectiveCount: 10, effectiveSource: 'license' }),
    ])

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limit-current-clients"]').text()).toBe('Currently 4')
    expect(wrapper.text()).toContain('Clients, runners and concurrent reviews are enforced')
  })

  // The authors row reads like the other three now that the month's rollup counts it.
  it('shows the current number on the authors row', async () => {
    summary.value = summaryWith([
      limit({ key: 'authorsPerMonth', allowance: 'count', licensedCount: 120, informationalCount: 11, effectiveCeiling: 'count', effectiveCount: 120, effectiveSource: 'license' }),
    ])

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limit-current-authorsPerMonth"]').text()).toBe('Currently 11')
  })

  // A payload that carries no number for a dimension says so. Showing zero would read as an installation with
  // no activity.
  it('says a dimension is not measured rather than showing zero', async () => {
    summary.value = summaryWith([
      limit({ key: 'authorsPerMonth', allowance: 'count', licensedCount: 120, effectiveCeiling: 'count', effectiveCount: 120, effectiveSource: 'license' }),
    ])

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limit-current-authorsPerMonth"]').text())
      .toBe('Not measured yet')
    expect(wrapper.get('[data-testid="license-limit-authorsPerMonth"]').find('.budget-meter').exists())
      .toBe(false)
  })

  // The excluded identities explain a count lower than the pull requests an operator can see, so the number
  // is said on the authors row.
  it('says how many automation identities were excluded this month', async () => {
    summary.value = summaryWith([
      limit({ key: 'authorsPerMonth', effectiveCeiling: 'unmetered', excludedAutomationCount: 3 }),
    ])

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limit-excluded-authorsPerMonth"]').text())
      .toBe('3 automation identities excluded this month')
  })

  it('says one excluded identity in the singular', async () => {
    summary.value = summaryWith([
      limit({ key: 'authorsPerMonth', effectiveCeiling: 'unmetered', excludedAutomationCount: 1 }),
    ])

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limit-excluded-authorsPerMonth"]').text())
      .toBe('1 automation identity excluded this month')
  })

  // Nothing excluded and nothing reported both leave the line out. A line reading zero would appear on every
  // installation that runs no automation.
  it.each([0, null])('leaves the excluded line out when the count is %s', async (excluded) => {
    summary.value = summaryWith([
      limit({ key: 'authorsPerMonth', effectiveCeiling: 'unmetered', excludedAutomationCount: excluded }),
      limit({ key: 'clients', informationalCount: 4 }),
    ])

    const wrapper = await mountCard()

    expect(wrapper.find('[data-testid="license-limit-excluded-authorsPerMonth"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="license-limit-excluded-clients"]').exists()).toBe(false)
  })

  it('names every limit it lists', async () => {
    summary.value = summaryWith([
      limit({ key: 'authorsPerMonth', effectiveCeiling: 'unmetered' }),
      limit({ key: 'clients', informationalCount: 0 }),
      limit({ key: 'runners', informationalCount: 0, effectiveCeiling: 'count', effectiveCount: 0 }),
      limit({ key: 'concurrentReviews', informationalCount: 0, effectiveCeiling: 'count', effectiveCount: 1 }),
    ])

    const wrapper = await mountCard()

    expect(wrapper.text()).toContain('Pull request authors per month')
    expect(wrapper.text()).toContain('Clients')
    expect(wrapper.text()).toContain('Runners')
    expect(wrapper.text()).toContain('Concurrent reviews')
  })

  // A percentage needs both a ceiling in force above zero and a known current number. Anywhere else a bar
  // would put a shape on an answer the data does not carry.
  it('draws a meter only where a percentage is meaningful', async () => {
    summary.value = summaryWith([
      limit({ key: 'clients', allowance: 'count', licensedCount: 10, informationalCount: 4, effectiveCeiling: 'count', effectiveCount: 10, effectiveSource: 'license' }),
      limit({ key: 'runners', allowance: 'unlimited', informationalCount: 3, effectiveCeiling: 'unlimited', effectiveSource: 'license' }),
      limit({ key: 'authorsPerMonth', informationalCount: 11, effectiveCeiling: 'unmetered' }),
      limit({ key: 'concurrentReviews', informationalCount: 0, effectiveCeiling: 'count', effectiveCount: 0 }),
    ])

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limit-clients"]').find('.budget-meter').exists()).toBe(true)
    expect(wrapper.get('[data-testid="license-limit-runners"]').find('.budget-meter').exists()).toBe(false)
    expect(wrapper.get('[data-testid="license-limit-authorsPerMonth"]').find('.budget-meter').exists()).toBe(false)
    expect(wrapper.get('[data-testid="license-limit-concurrentReviews"]').find('.budget-meter').exists()).toBe(false)
  })

  // The bar fills towards the ceiling in force. A license stating more than the installation is entitled to
  // would otherwise draw headroom an enrollment does not have.
  it('measures the meter against the ceiling in force rather than the stated one', async () => {
    summary.value = summaryWith([
      limit({ key: 'clients', allowance: 'count', licensedCount: 100, informationalCount: 4, effectiveCeiling: 'count', effectiveCount: 4, effectiveSource: 'community' }),
    ])

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limit-clients"]').find('.budget-meter').classes())
      .toContain('is-danger')
  })

  it('marks a limit that is reached', async () => {
    summary.value = summaryWith([
      limit({ key: 'clients', allowance: 'count', licensedCount: 4, informationalCount: 4, effectiveCeiling: 'count', effectiveCount: 4, effectiveSource: 'license' }),
    ])

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limit-clients"]').find('.budget-meter').classes())
      .toContain('is-danger')
  })

  // A payload that carries no effective ceiling is said to carry none. Showing it as unlimited or as a zero
  // would report a ceiling the backend never sent.
  it('reports a missing effective ceiling rather than inventing one', async () => {
    summary.value = summaryWith([
      limit({ key: 'clients', allowance: 'count', licensedCount: 10, informationalCount: 4, effectiveCeiling: null, effectiveSource: null }),
    ])

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limit-effective-clients"]').text()).toBe('Not reported')
    expect(wrapper.find('[data-testid="license-limit-source-clients"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="license-limit-stated-clients"]').text()).toBe('The license states 10')
  })

  it('does not render a missing count ceiling as zero', async () => {
    summary.value = summaryWith([
      limit({ key: 'clients', allowance: 'count', licensedCount: null, effectiveCeiling: 'count', effectiveCount: null }),
    ])

    const wrapper = await mountCard()
    const row = wrapper.get('[data-testid="license-limit-clients"]').text()

    expect(row).toContain('Not reported')
    expect(row).not.toContain('The license states 0')
  })

  it('reports an empty payload rather than an empty card', async () => {
    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limits-empty"]').text()).toBe('No limits were reported.')
  })

  // Below the licensed number the panel says what is left of it, so an operator can see how much of the month's
  // allowance is unused without subtracting the two numbers.
  it('says what is left of the licensed author number while the month is below it', async () => {
    summary.value = summaryWith([authorsLimit()], {
      authorOverage: { licensedCount: 120, observedCount: 11, isInOverage: false },
    })

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-author-headroom"]').text())
      .toBe('109 of 120 remaining this month')
    expect(wrapper.find('[data-testid="license-author-overage"]').exists()).toBe(false)
  })

  // A month that has reached the number exactly is not above it, so the headroom line is still the one due and
  // it reads nothing left rather than being left out.
  it('reads no headroom left on a month that has reached the licensed number', async () => {
    summary.value = summaryWith([authorsLimit()], {
      authorOverage: { licensedCount: 120, observedCount: 120, isInOverage: false },
    })

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-author-headroom"]').text())
      .toBe('0 of 120 remaining this month')
    expect(wrapper.find('[data-testid="license-author-overage"]').exists()).toBe(false)
  })

  // Above the number both counts are named, and the panel says the installation kept running. The whole line is
  // asserted against the copy function the panel renders, so the rendering and the wording stay one sentence.
  it('names both counts and says nothing has been withheld once the month is above the number', async () => {
    summary.value = summaryWith([authorsLimit()], {
      authorOverage: { licensedCount: 5, observedCount: 12, isInOverage: true },
    })

    const wrapper = await mountCard()
    const text = wrapper.get('[data-testid="license-author-overage"]').text()

    expect(text).toBe(authorOverageLabel({ licensedCount: 5, observedCount: 12, isInOverage: true }))
    expect(text).toContain('12 pull request authors')
    expect(text).toContain('5')
    expect(text).toContain('Nothing has been withheld, delayed or degraded')
    expect(text).toContain('not enforced')
    expect(wrapper.find('[data-testid="license-author-headroom"]').exists()).toBe(false)
  })

  // A single author over a license that states none is still an overage, and the line names one author rather
  // than carrying a bracketed plural.
  it('names one author in the singular', async () => {
    summary.value = summaryWith([authorsLimit()], {
      authorOverage: { licensedCount: 0, observedCount: 1, isInOverage: true },
    })

    const wrapper = await mountCard()
    const text = wrapper.get('[data-testid="license-author-overage"]').text()

    expect(text).toContain('1 pull request author this month')
  })

  // A license that omits the author limit or states it as unlimited carries no comparison. The peak keeps the
  // section on the page, so what is asserted here is the two lines being suppressed rather than the section
  // being gone.
  it.each(['absent', 'unlimited'] as const)(
    'leaves both lines out when the author allowance is %s',
    async (allowance) => {
      summary.value = summaryWith(
        [limit({
          key: 'authorsPerMonth',
          allowance,
          informationalCount: 19,
          effectiveCeiling: allowance === 'unlimited' ? 'unlimited' : 'unmetered',
          effectiveSource: allowance === 'unlimited' ? 'license' : 'community',
        })],
        {
          authorOverage: null,
          authorPeakMonth: { month: '2026-04-01', authorCount: 19 },
        },
      )

      const wrapper = await mountCard()

      expect(wrapper.find('[data-testid="license-author-readings"]').exists()).toBe(true)
      expect(wrapper.find('[data-testid="license-author-headroom"]').exists()).toBe(false)
      expect(wrapper.find('[data-testid="license-author-overage"]').exists()).toBe(false)
    },
  )

  it('names the busiest month of the trailing year', async () => {
    summary.value = summaryWith([authorsLimit()], {
      authorPeakMonth: { month: '2026-04-01', authorCount: 19 },
    })

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-author-peak"]').text())
      .toBe(`Peak month: ${peakMonthName('2026-04-01T00:00:00Z')} with 19 authors`)
  })

  it('names a peak month holding one author in the singular', async () => {
    summary.value = summaryWith([authorsLimit()], {
      authorPeakMonth: { month: '2026-04-01', authorCount: 1 },
    })

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-author-peak"]').text())
      .toBe(`Peak month: ${peakMonthName('2026-04-01T00:00:00Z')} with 1 author`)
  })

  // No peak, and a month this build cannot read as a date, both leave the line out rather than render a month
  // name the payload does not support.
  it.each([null, ''])('leaves the peak line out when the month is %s', async (month) => {
    summary.value = summaryWith([authorsLimit()], {
      authorPeakMonth: month === null ? null : { month, authorCount: 19 },
    })

    const wrapper = await mountCard()

    expect(wrapper.find('[data-testid="license-author-peak"]').exists()).toBe(false)
  })

  // The section carries a heading naming the metric, so its lines are not read as belonging to the four rows
  // above it. With nothing to say it is left out rather than shown as a heading on its own.
  it('names the metric the readings belong to', async () => {
    summary.value = summaryWith([authorsLimit()], {
      authorPeakMonth: { month: '2026-04-01', authorCount: 19 },
    })

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-author-readings"]').text())
      .toContain('Pull request authors per month')
  })

  it('leaves the section out when the payload carries none of the readings', async () => {
    summary.value = summaryWith([authorsLimit()])

    const wrapper = await mountCard()

    expect(wrapper.find('[data-testid="license-author-readings"]').exists()).toBe(false)
  })

  // A payload that reports no limits gets the line saying so and nothing else. Readings about a limit that is
  // not on the card would follow that line and read as a contradiction.
  it('leaves the section out on a payload that reports no limits', async () => {
    summary.value = summaryWith([], {
      authorOverage: { licensedCount: 120, observedCount: 11, isInOverage: false },
      authorPeakMonth: { month: '2026-04-01', authorCount: 19 },
    })

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limits-empty"]').text()).toBe('No limits were reported.')
    expect(wrapper.find('[data-testid="license-author-readings"]').exists()).toBe(false)
  })

  // The readings belong to the author metric alone. The other three rows read as they did, and none of them
  // gains a line about headroom or a peak month.
  it('leaves the other rows as they were', async () => {
    summary.value = summaryWith(
      [
        authorsLimit(),
        limit({ key: 'clients', allowance: 'count', licensedCount: 10, informationalCount: 4, effectiveCeiling: 'count', effectiveCount: 10, effectiveSource: 'license' }),
        limit({ key: 'runners', allowance: 'unlimited', informationalCount: 3, effectiveCeiling: 'unlimited', effectiveSource: 'license' }),
        limit({ key: 'concurrentReviews', allowance: 'count', licensedCount: 6, informationalCount: 5, effectiveCeiling: 'count', effectiveCount: 6, effectiveSource: 'license' }),
      ],
      {
        authorOverage: { licensedCount: 120, observedCount: 11, isInOverage: false },
        authorPeakMonth: { month: '2026-04-01', authorCount: 19 },
      },
    )

    const wrapper = await mountCard()

    expect(wrapper.get('[data-testid="license-limit-effective-clients"]').text()).toBe('10')
    expect(wrapper.get('[data-testid="license-limit-stated-clients"]').text()).toBe('The license states 10')
    expect(wrapper.get('[data-testid="license-limit-current-clients"]').text()).toBe('Currently 4')
    expect(wrapper.get('[data-testid="license-limit-current-runners"]').text()).toBe('Currently 3')
    expect(wrapper.get('[data-testid="license-limit-current-concurrentReviews"]').text()).toBe('Currently 5')

    for (const key of ['clients', 'runners', 'concurrentReviews', 'authorsPerMonth']) {
      const row = wrapper.get(`[data-testid="license-limit-${key}"]`).text()

      expect(row).not.toContain('remaining this month')
      expect(row).not.toContain('Peak month')
    }
  })
})
