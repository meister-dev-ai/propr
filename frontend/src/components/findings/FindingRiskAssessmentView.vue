<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="finding-risk" role="group" aria-label="Finding risk assessment">
    <div class="finding-risk-values">
      <span class="finding-risk-chip" :class="`finding-risk-chip--${risk.riskSeverity}`">Risk severity: {{ riskLabel(risk.riskSeverity) }}</span>
      <span class="finding-risk-chip">Impact: {{ riskLabel(risk.impact) }}</span>
      <span class="finding-risk-chip">Likelihood: {{ riskLabel(risk.likelihood) }}</span>
      <span class="finding-risk-chip">Confidence: {{ riskLabel(risk.confidence) }}</span>
    </div>
    <p v-if="!authoritative && assessment" class="finding-risk-note">Proposed assessment. Risk severity and confidence are unassessed until the evidence is validated.</p>
    <p v-if="risk.impactReason" class="finding-risk-reason"><strong>Impact:</strong> {{ risk.impactReason }}</p>
    <p v-if="risk.confidenceReason" class="finding-risk-reason"><strong>Confidence:</strong> {{ risk.confidenceReason }}</p>
    <p v-if="risk.likelihoodReason" class="finding-risk-reason"><strong>Likelihood:</strong> {{ risk.likelihoodReason }}</p>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { findingRisk, riskLabel, type FindingRiskAssessment } from '@/features/reviews/findingRisk'
const props = withDefaults(defineProps<{ assessment?: FindingRiskAssessment | null; authoritative?: boolean }>(), { authoritative: true })
const risk = computed(() => findingRisk(props.assessment, props.authoritative))
</script>

<style scoped>
.finding-risk { margin-block: 0.45rem; min-width: 0; }
.finding-risk-values { display: flex; flex-wrap: wrap; gap: 0.3rem; }
.finding-risk-chip { border: 1px solid var(--color-border); border-radius: var(--radius-pill); padding: 0.1rem 0.5rem; color: var(--color-text-muted); font-size: 0.75rem; }
.finding-risk-chip--critical { color: var(--color-danger); font-weight: 700; }
.finding-risk-chip--high { color: var(--color-danger); }
.finding-risk-chip--medium { color: var(--color-warning); }
.finding-risk-chip--low { color: var(--color-info); }
.finding-risk-note, .finding-risk-reason { margin: 0.3rem 0 0; color: var(--color-text-muted); font-size: 0.8rem; line-height: 1.5; overflow-wrap: anywhere; }
.finding-risk-reason strong { color: var(--color-text); }
</style>
