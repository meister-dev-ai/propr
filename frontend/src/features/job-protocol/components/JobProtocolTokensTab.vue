<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="tokens-tab-view">
        <section class="section-card">
            <div class="section-card-header">
                <h3 class="section-title">Token Usage by Tier</h3>
            </div>
            <div class="section-inner">
                <p class="section-description">
                    Breakdown of token consumption across administrative, memory, and review tiers.
                </p>
                <TokenBreakdownTable
                    v-if="vm.protocolTokenBreakdown.length > 0 || vm.jobDetail"
                    :breakdown="vm.protocolTokenBreakdown"
                    :breakdown-consistent="vm.protocolBreakdownConsistent"
                />
                <p v-else class="empty-state">No detailed token breakdown available for this job.</p>
            </div>
        </section>
    </div>
</template>

<script setup lang="ts">
import TokenBreakdownTable from '@/components/usage/TokenBreakdownTable.vue'
import type { JobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'

defineProps<{ vm: JobProtocolViewModel }>()
</script>

<style scoped>
.tokens-tab-view {
    padding: 2rem;
    max-width: 60rem;
    margin: 0 auto;
}

.section-inner {
    padding: 1.5rem;
}

.section-description {
    color: var(--color-text-muted);
    font-size: 0.9rem;
    margin-bottom: 1.5rem;
}
</style>
