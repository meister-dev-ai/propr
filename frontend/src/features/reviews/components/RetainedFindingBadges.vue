<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
    <div v-if="finding" class="finding-badges" data-testid="retained-finding-badges" role="group" aria-label="Finding details">
        <span class="finding-badge" :class="`finding-severity-${finding.severity.toLowerCase()}`">{{ findingLabel(finding.severity) }}</span>
        <span v-for="kind in finding.coreTags" :key="kind" class="finding-badge finding-kind" :title="kind">{{ findingLabel(kind) }}</span>
        <span class="finding-badge">{{ finding.disposition ? findingLabel(finding.disposition) : 'Outcome pending' }}</span>
        <span v-if="finding.rejectionReason" class="finding-badge finding-reason" :title="`Recorded rejection reason: ${findingLabel(finding.rejectionReason)}`">
            {{ findingOutcome(finding) === 'duplicate' ? 'Duplicate' : findingLabel(finding.rejectionReason) }}
        </span>
    </div>
</template>

<script setup lang="ts">
import type { CodeInsightFinding } from '@/services/codeInsightsAnalyticsService'
import { findingLabel, findingOutcome } from './reviewBrowserFindings'
defineProps<{ finding: CodeInsightFinding | null }>()
</script>

<style scoped>
.finding-badges {
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
    margin-bottom: 0.4rem;
    font-family: inherit;
}
.finding-badge {
    border: 1px solid var(--color-border);
    border-radius: var(--radius-pill);
    padding: 0.05rem 0.5rem;
    font-size: 0.72rem;
    color: var(--color-text-muted);
}
.finding-kind {
    color: var(--color-accent);
}
.finding-severity-error {
    color: var(--color-danger);
}
.finding-severity-warning, .finding-reason {
    color: var(--color-warning);
}
.finding-severity-suggestion {
    color: var(--color-accent);
}
.finding-severity-info {
    color: var(--color-info);
}
</style>
