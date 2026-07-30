<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="insights-panel" aria-labelledby="insights-reasons-heading">
    <header class="panel-header">
      <div>
        <p class="panel-kicker">Rejections</p>
        <h3 id="insights-reasons-heading">Why findings were turned down</h3>
        <p class="panel-copy">
          A precision figure says how often the reviewer was turned down. This says what to do about it. A
          reviewer that invents problems needs a better prompt, one that argues with deliberate decisions needs
          this codebase's conventions, and one that repeats another tool needs to be told what that tool covers.
          Findings about how the code behaves and findings about how it will age are turned down for different
          reasons, so the two are worth reading separately.
        </p>
      </div>

      <p class="reasons-headline">
        <span class="reasons-value">{{ reasons.rejections }}</span>
        <span class="reasons-label">rejection{{ reasons.rejections === 1 ? '' : 's' }} in this window</span>
      </p>
    </header>

    <!-- Its own failure, in its own section: every other number on the surface stays readable without it. -->
    <p v-if="error" class="panel-error" role="alert">
      {{ error }}
      <button type="button" class="retry-button" @click="emit('retry')">Try again</button>
    </p>

    <p v-else-if="reasons.rejections === 0" class="panel-empty">
      Nothing was rejected in this window. That is a different thing from nothing having been classified.
    </p>

    <template v-else>
      <!-- Within a class rather than across the whole set: the two classes are turned down at similar rates for
           entirely different reasons, and one combined distribution averages that difference away. -->
      <div v-if="classChoices.length > 1" class="class-switch" role="group" aria-label="Kind of concern">
        <button
          v-for="choice in classChoices"
          :key="choice.key"
          type="button"
          class="class-tab"
          :class="{ 'class-tab--active': selectedClass === choice.key }"
          :aria-pressed="selectedClass === choice.key"
          @click="selectedClass = choice.key"
        >
          {{ choice.label }}
          <span class="class-count">{{ choice.rejections }}</span>
        </button>
      </div>

      <ul class="reason-list">
        <li v-for="row in rows" :key="row.reason">
          <button
            type="button"
            class="reason-row"
            :aria-label="`Show the ${LABELS[row.reason]} rejections`"
            @click="emit('drill', row.reason, LABELS[row.reason])"
          >
            <span class="reason-name">{{ LABELS[row.reason] }}</span>
            <span class="reason-bar" aria-hidden="true">
              <span class="reason-fill" :style="{ width: `${share(row.count)}%` }"></span>
            </span>
            <span class="reason-count">{{ row.count }}</span>
            <span class="reason-share">{{ share(row.count).toFixed(0) }}%</span>
          </button>
          <p class="reason-copy">{{ MEANINGS[row.reason] }}</p>
        </li>
      </ul>

      <!-- Reported rather than folded into a reason: an unexplained rejection is not evidence for any
           particular explanation, and the outcomes recorded before reasons existed carry none at all. -->
      <p v-if="shown.unclassified > 0" class="unclassified-note" role="note">
        <i class="fi fi-rr-interrogation" aria-hidden="true"></i>
        <span>
          {{ shown.unclassified }} of {{ shown.rejections }} rejection{{ shown.rejections === 1 ? '' : 's' }}
          carr{{ shown.unclassified === 1 ? 'ies' : 'y' }} no reason. Either the discussion gave the classifier
          nothing to go on, or the outcome was recorded before reasons were.
        </span>
      </p>

      <EstimateNotice />
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import EstimateNotice from '@/features/code-insights/components/EstimateNotice.vue'
import type {
  CodeInsightConcernClass,
  CodeInsightRejectionReason,
  CodeInsightRejectionReasons,
} from '@/services/codeInsightsAnalyticsService'

const props = defineProps<{ reasons: CodeInsightRejectionReasons; error?: string | null }>()
const emit = defineEmits<{
  drill: [reason: CodeInsightRejectionReason, label: string]
  retry: []
}>()

/** Which slice of the rejections the bars describe: everything, or one kind of concern. */
type ClassChoice = 'all' | CodeInsightConcernClass | 'untyped'

const selectedClass = ref<ClassChoice>('all')

const LABELS: Record<CodeInsightRejectionReason, string> = {
  Wrong: 'Reviewer was wrong',
  DesignTradeOff: 'Deliberate trade-off',
  DeveloperPreference: 'Developer preference',
  OutOfScope: 'Out of scope',
  Redundant: 'Already covered',
}

const MEANINGS: Record<CodeInsightRejectionReason, string> = {
  Wrong: 'The finding did not describe a real problem. This is the one that calls for a prompt or model change.',
  DesignTradeOff: 'Correct, and the code is that way on purpose. The reviewer needs the local conventions.',
  DeveloperPreference: 'Correct, and the team prefers its own way. Taste rather than consequence.',
  OutOfScope: 'Correct, and not part of this change. Pre-existing, or tracked elsewhere.',
  Redundant: 'Correct, and something else already covers it. Another tool, or another finding.',
}

const CLASS_LABELS: Record<CodeInsightConcernClass | 'untyped', string> = {
  Functional: 'Functional',
  Evolvability: 'Evolvability',
  untyped: 'No type yet',
}

/**
 * The whole set first, then one tab per class present. A class with no rejections is absent rather than shown
 * empty, and the single-class case hides the switch entirely.
 */
const classChoices = computed(() => [
  { key: 'all' as ClassChoice, label: 'All rejections', rejections: props.reasons.rejections },
  ...props.reasons.byConcernClass.map((row) => ({
    key: (row.concernClass ?? 'untyped') as ClassChoice,
    label: CLASS_LABELS[row.concernClass ?? 'untyped'],
    rejections: row.rejections,
  })),
])

/** The slice on screen: the whole window, or the selected class. */
const shown = computed(() => {
  if (selectedClass.value === 'all') {
    return props.reasons
  }

  const match = props.reasons.byConcernClass.find(
    (row) => (row.concernClass ?? 'untyped') === selectedClass.value,
  )

  return match ?? { reasons: [], unclassified: 0, rejections: 0 }
})

const rows = computed(() => shown.value.reasons.filter((row) => row.reason in LABELS))

/** Share of the shown rejections that carry a reason, so the bars compare like with like. */
const share = (count: number): number => {
  const classified = shown.value.rejections - shown.value.unclassified
  return classified === 0 ? 0 : (count / classified) * 100
}
</script>

<style scoped>
@import '@/features/code-insights/insights-panel.css';

.reasons-headline {
  margin: 0;
  text-align: right;
}

.reasons-value {
  display: block;
  font-size: 1.9rem;
  font-weight: 800;
  line-height: 1;
  color: var(--color-text);
  font-variant-numeric: tabular-nums;
}

.reasons-label {
  display: block;
  max-width: 18ch;
  font-size: 0.75rem;
  color: var(--color-text-muted);
}

.class-switch {
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem;
}

.class-tab {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  padding: 0.35rem 0.7rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-pill);
  background: transparent;
  color: var(--color-text-muted);
  font: inherit;
  font-size: 0.78rem;
  font-weight: 600;
  cursor: pointer;
}

.class-tab:hover,
.class-tab:focus-visible {
  border-color: rgba(34, 211, 238, 0.4);
  color: var(--color-text);
}

.class-tab--active {
  border-color: rgba(34, 211, 238, 0.5);
  background: rgba(34, 211, 238, 0.1);
  color: var(--color-text);
}

.class-count {
  font-variant-numeric: tabular-nums;
  opacity: 0.75;
}

.reason-list {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  margin: 0;
  padding: 0;
  list-style: none;
}

.reason-row {
  display: grid;
  grid-template-columns: minmax(9rem, 14rem) 1fr auto auto;
  align-items: center;
  gap: 0.75rem;
  width: 100%;
  padding: 0.4rem 0.5rem;
  border: 1px solid transparent;
  border-radius: 0.5rem;
  background: transparent;
  color: var(--color-text);
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.reason-row:hover,
.reason-row:focus-visible {
  border-color: rgba(34, 211, 238, 0.4);
  background: rgba(34, 211, 238, 0.08);
}

.reason-name {
  font-size: 0.85rem;
  font-weight: 600;
}

.reason-bar {
  display: block;
  height: 0.55rem;
  border-radius: var(--radius-pill);
  background: rgba(148, 163, 184, 0.18);
  overflow: hidden;
}

.reason-fill {
  display: block;
  height: 100%;
  border-radius: inherit;
  background: var(--color-accent);
}

.reason-count,
.reason-share {
  font-size: 0.85rem;
  font-variant-numeric: tabular-nums;
}

.reason-share {
  min-width: 3ch;
  text-align: right;
  color: var(--color-text-muted);
}

.reason-copy {
  margin: 0.1rem 0 0 0.5rem;
  font-size: 0.75rem;
  line-height: 1.35;
  color: var(--color-text-muted);
}

.unclassified-note {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  margin: 0;
  padding: 0.6rem 0.75rem;
  border: 1px solid var(--color-border);
  border-radius: 0.6rem;
  background: rgba(148, 163, 184, 0.08);
  font-size: 0.8rem;
  line-height: 1.4;
  color: var(--color-text);
}

.panel-error {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  margin: 0;
  padding: 0.6rem 0.75rem;
  border: 1px solid rgba(239, 68, 68, 0.35);
  border-radius: 0.6rem;
  background: rgba(239, 68, 68, 0.08);
  color: var(--color-text);
  font-size: 0.85rem;
}

.retry-button {
  padding: 0.25rem 0.6rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-xs);
  background: transparent;
  color: var(--color-accent);
  font: inherit;
  font-size: 0.8rem;
  cursor: pointer;
}
</style>
