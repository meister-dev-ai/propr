<template>
    <div class="review-history-view">
        <div class="toolbar">
            <RouterLink class="back-link" to="/">← Back to clients</RouterLink>
            <h2>Review History</h2>
        </div>
        <ReviewHistorySection />
    </div>
</template>

<script lang="ts" setup>
import { RouterLink } from 'vue-router'
import ReviewHistorySection from '@/components/ReviewHistorySection.vue'
</script>

<style scoped>
.review-history-view {
    padding: 1rem;
}

.toolbar {
    display: flex;
    align-items: center;
    gap: 1rem;
    margin-bottom: 1.5rem;
}

.toolbar h2 {
    margin: 0;
}

.back-link {
    font-size: 0.9rem;
    color: #555;
    text-decoration: none;
}

.back-link:hover {
    text-decoration: underline;
}
</style>
