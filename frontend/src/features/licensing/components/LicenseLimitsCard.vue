<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented. -->

<script setup lang="ts">
/**
 * The quantitative limits, each showing the ceiling the installation is held to, where that ceiling came
 * from, what the license states, and what the installation currently holds.
 *
 * The ceiling in force is shown first, because an enforcement refusal quotes it. It is not always the number
 * the license states: a limit the license leaves out is held to the community value, and a license whose term
 * and grace window have both ended no longer supplies its stated numbers. Both are shown, so an operator
 * reading a refusal can see where the number came from.
 *
 * The current numbers are counts taken when the page loads rather than a live reading, which is why the copy
 * says "currently". All four dimensions carry one: clients, runners and reviews executing come from the counts
 * the enforcement points admit against, and authors per month from the month's rollup. Three of the four are
 * enforced where the resource is created or claimed, and the subtitle says which, so a reader is not left to
 * assume the whole card is advisory. A payload that carries no number for a dimension says so rather than
 * showing zero, because zero is a real count.
 *
 * Authors per month additionally says how many automation identities the exclusion rules kept out of the
 * month, because that number explains a count lower than the pull requests an operator can see. It appears
 * only where something was excluded. No meter is drawn for that row while nothing counts it, because a bar
 * needs a ceiling to fill towards.
 *
 * The author metric carries two further readings, and they are shown under the four rows rather than inside the
 * authors row: where the month stands against the licensed number, and the busiest month of the trailing year.
 * The first is a sentence, because an operator reading a month above the number has to be told that nothing was
 * withheld, and a sentence set in one column of a four-column grid wraps to several times the height of the rows
 * beside it. Both readings sit under a heading naming the metric, so they are not read as belonging to the grid
 * as a whole.
 */
import { computed } from 'vue'
import BudgetMeter from '@/features/clients/components/BudgetMeter.vue'
import { useLicensing } from '@/composables/useLicensing'
import type { LicenseLimit } from '@/services/licensingService'
import {
  authorHeadroomLabel,
  authorOverageLabel,
  authorPeakMonthLabel,
  effectiveCeilingLabel,
  effectiveSourceLabel,
  excludedAutomationLabel,
  limitLabel,
  statedAllowanceLabel,
} from '@/features/licensing/licensingCopy'

const { summary } = useLicensing()

const limits = computed(() => summary.value?.limits ?? [])

const authorHeadroomLine = computed(() => authorHeadroomLabel(summary.value?.authorOverage ?? null))

const authorOverageLine = computed(() => authorOverageLabel(summary.value?.authorOverage ?? null))

const authorPeakLine = computed(() => authorPeakMonthLabel(summary.value?.authorPeakMonth ?? null))

/**
 * The section is left out where the payload carries none of the three lines, rather than shown as a heading with
 * nothing under it. It is also left out on a payload that reports no limits at all, because the readings belong
 * to a limit that is not there and they would follow the line saying nothing was reported.
 */
const showsAuthorReadings = computed(
  () =>
    limits.value.length > 0
    && (
      authorHeadroomLine.value !== null
      || authorOverageLine.value !== null
      || authorPeakLine.value !== null
    ),
)

/**
 * Carries its own prefix, because the two cases do not share one. "Currently 4" is a reading; "Currently not
 * measured yet" is not a sentence, and the case without a number has to stand on its own. Every dimension
 * carries a number on the administration read, so the second case is a payload that gathers no counts and a
 * host that cannot measure the dimension.
 */
function currentLabel(limit: LicenseLimit): string {
  return limit.informationalCount === null ? 'Not measured yet' : `Currently ${limit.informationalCount}`
}

/**
 * A percentage is meaningful only where the ceiling in force is a number above zero and the current number is
 * known. Anywhere else the bar would put a shape on an answer the data does not carry.
 *
 * Measured against the ceiling in force rather than the stated one, so the bar fills towards the number a
 * refusal would name.
 */
function usagePercent(limit: LicenseLimit): number | null {
  if (limit.effectiveCeiling !== 'count' || limit.effectiveCount === null || limit.effectiveCount <= 0) {
    return null
  }

  if (limit.informationalCount === null) {
    return null
  }

  return (limit.informationalCount / limit.effectiveCount) * 100
}

function usageStatus(percent: number): 'ok' | 'warning' | 'danger' {
  if (percent >= 100) {
    return 'danger'
  }

  return percent >= 80 ? 'warning' : 'ok'
}
</script>

<template>
  <section class="section-card licensing-limits-card">
    <div class="section-card-header">
      <div>
        <h3>Limits</h3>
        <p class="licensing-subtitle">
          What this installation is held to, beside what its license states and what it currently holds.
          Clients, runners and concurrent reviews are enforced; pull request authors per month is reported.
        </p>
      </div>
    </div>

    <div class="section-card-body">
      <p v-if="limits.length === 0" class="licensing-empty" data-testid="license-limits-empty">
        No limits were reported.
      </p>

      <div v-else class="licensing-limits-grid">
        <article
          v-for="limit in limits"
          :key="limit.key"
          class="licensing-limit"
          :data-testid="`license-limit-${limit.key}`"
        >
          <div class="licensing-limit-label">{{ limitLabel(limit.key) }}</div>
          <div class="licensing-limit-value" :data-testid="`license-limit-effective-${limit.key}`">
            {{ effectiveCeilingLabel(limit) }}
          </div>
          <div
            v-if="effectiveSourceLabel(limit) !== null"
            class="licensing-limit-source"
            :data-testid="`license-limit-source-${limit.key}`"
          >
            {{ effectiveSourceLabel(limit) }}
          </div>
          <div class="licensing-limit-stated" :data-testid="`license-limit-stated-${limit.key}`">
            {{ statedAllowanceLabel(limit) }}
          </div>
          <div class="licensing-limit-current" :data-testid="`license-limit-current-${limit.key}`">
            {{ currentLabel(limit) }}
          </div>
          <div
            v-if="excludedAutomationLabel(limit) !== null"
            class="licensing-limit-excluded"
            :data-testid="`license-limit-excluded-${limit.key}`"
          >
            {{ excludedAutomationLabel(limit) }}
          </div>
          <BudgetMeter
            v-if="usagePercent(limit) !== null"
            class="licensing-limit-meter"
            :percent="usagePercent(limit)!"
            :status="usageStatus(usagePercent(limit)!)"
          />
        </article>
      </div>

      <section
        v-if="showsAuthorReadings"
        class="licensing-author-readings"
        data-testid="license-author-readings"
      >
        <h4>{{ limitLabel('authorsPerMonth') }}</h4>

        <p
          v-if="authorHeadroomLine !== null"
          class="licensing-author-reading"
          data-testid="license-author-headroom"
        >
          {{ authorHeadroomLine }}
        </p>
        <p
          v-if="authorOverageLine !== null"
          class="licensing-author-reading"
          data-testid="license-author-overage"
        >
          {{ authorOverageLine }}
        </p>
        <p
          v-if="authorPeakLine !== null"
          class="licensing-author-reading"
          data-testid="license-author-peak"
        >
          {{ authorPeakLine }}
        </p>
      </section>
    </div>
  </section>
</template>

<style scoped>
.licensing-subtitle {
  color: var(--color-text-muted);
  font-size: 0.8rem;
  margin: 0.25rem 0 0;
}

.licensing-limits-card .section-card-body {
  padding: 1.25rem;
}

.licensing-empty {
  color: var(--color-text-muted);
  margin: 0;
}

.licensing-limits-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr));
  gap: 1rem;
}

.licensing-limit {
  border: 1px solid var(--color-border);
  border-radius: 0.9rem;
  background: var(--color-surface);
  padding: 1rem;
}

.licensing-limit-label {
  color: var(--color-text-muted);
  font-size: 0.78rem;
  margin-bottom: 0.35rem;
}

.licensing-limit-value {
  font-size: 1.5rem;
  font-weight: 600;
  line-height: 1.2;
}

.licensing-limit-source {
  color: var(--color-text-muted);
  font-size: 0.78rem;
  margin-top: 0.2rem;
}

.licensing-limit-stated,
.licensing-limit-current,
.licensing-limit-excluded {
  color: var(--color-text-muted);
  font-size: 0.8rem;
  margin-top: 0.25rem;
}

.licensing-limit-meter {
  margin-top: 0.6rem;
}

.licensing-author-readings {
  margin-top: 1.25rem;
  padding-top: 1rem;
  border-top: 1px solid var(--color-border);
}

.licensing-author-readings h4 {
  margin: 0;
  font-size: 0.9rem;
}

.licensing-author-reading {
  color: var(--color-text-muted);
  font-size: 0.8rem;
  line-height: 1.55;
  margin: 0.35rem 0 0;
}
</style>
