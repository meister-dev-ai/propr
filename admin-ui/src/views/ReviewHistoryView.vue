<template>
    <div class="review-history-view">
        <div class="toolbar">
            <h2>Review History</h2>
        </div>
        <ReviewHistorySection />
    </div>
</template>

<script lang="ts" setup>
import ReviewHistorySection from '@/components/ReviewHistorySection.vue'
</script>

<style scoped>
.toolbar {
    display: flex;
    align-items: center;
    gap: 1rem;
    margin-bottom: 2rem;
}

.toolbar h2 {
    margin: 0;
}
</style>
