<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
    <div class="budget-meter" :class="`is-${status}`">
        <div class="budget-meter-fill" :style="{ width: `${clampedPercent}%` }"></div>
    </div>
</template>

<script lang="ts" setup>
import { computed } from 'vue'

const props = defineProps<{
    /** Fill percentage (0-100); values outside the range are clamped. */
    percent: number
    /** Drives the fill colour: ok (green), warning (amber), danger (red). */
    status: 'ok' | 'warning' | 'danger'
}>()

const clampedPercent = computed(() => Math.max(0, Math.min(100, props.percent)))
</script>

<style scoped>
.budget-meter {
    height: 0.6rem;
    border-radius: 999px;
    background: var(--color-muted-soft);
    overflow: hidden;
}

.budget-meter-fill {
    height: 100%;
    border-radius: 999px;
    transition: width 0.3s ease;
}

.budget-meter.is-ok .budget-meter-fill {
    background: var(--color-success);
}

.budget-meter.is-warning .budget-meter-fill {
    background: var(--color-warning);
}

.budget-meter.is-danger .budget-meter-fill {
    background: var(--color-danger);
}
</style>
