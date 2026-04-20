<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="provider-detail-panel">
    <div class="provider-detail-panel-header">
      <h4>Reviewer Identity</h4>
      <span v-if="reviewerIdentity" class="chip chip-success chip-sm">Configured</span>
      <span v-else class="chip chip-muted chip-sm">Not configured</span>
    </div>
    <div v-if="reviewerIdentity" class="provider-reviewer-current">
      <strong>{{ reviewerIdentity.displayName }}</strong>
      <p>{{ reviewerIdentity.login }}</p>
    </div>
    <p v-else class="filters-empty-hint">No reviewer identity saved for this connection.</p>
    <div class="provider-form-grid provider-form-grid-tight">
      <div class="form-field provider-form-grid-full">
        <label>Search</label>
        <input v-model="reviewerSearchModel" type="text" placeholder="Search login or bot account" />
      </div>
    </div>
    <p v-if="error" class="error">{{ error }}</p>
    <div class="form-actions">
      <button class="btn-secondary btn-sm provider-reviewer-resolve" :disabled="busy" @click="emit('resolve')">
        {{ busy ? 'Resolving…' : 'Resolve Candidates' }}
      </button>
      <button v-if="reviewerIdentity" class="btn-danger btn-sm" :disabled="busy" @click="emit('clear')">
        Clear Identity
      </button>
    </div>
    <div style="margin-top: 1.5rem;">
      <ul v-if="reviewerCandidates.length" class="provider-candidate-list" style="margin-top: 0;">
        <li
          v-for="candidate in reviewerCandidates"
          :key="candidate.externalUserId"
          class="provider-candidate-item"
          :class="{ selected: selectedReviewerExternalUserIdModel === candidate.externalUserId }"
          @click="selectedReviewerExternalUserIdModel = candidate.externalUserId"
        >
          <div>
            <strong>{{ candidate.displayName }}</strong>
            <p>{{ candidate.login }}</p>
          </div>
          <span v-if="candidate.isBot" class="chip chip-info chip-sm">Bot</span>
        </li>
      </ul>
    </div>
    <div v-if="selectedReviewerCandidate" class="form-actions">
      <button class="btn-primary btn-sm provider-reviewer-save" :disabled="busy" @click="emit('save')">
        Save Reviewer Identity
      </button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { ClientReviewerIdentityDto, ResolvedReviewerIdentityResponse } from '@/services/providerConnectionsService'

const props = withDefaults(defineProps<{
  reviewerIdentity: ClientReviewerIdentityDto | null
  reviewerCandidates: ResolvedReviewerIdentityResponse[]
  reviewerSearch: string
  selectedReviewerExternalUserId: string | null
  busy?: boolean
  error?: string
}>(), {
  busy: false,
  error: '',
})

const emit = defineEmits<{
  (e: 'update:reviewerSearch', value: string): void
  (e: 'update:selectedReviewerExternalUserId', value: string | null): void
  (e: 'resolve'): void
  (e: 'clear'): void
  (e: 'save'): void
}>()

const reviewerSearchModel = computed({
  get: () => props.reviewerSearch,
  set: value => emit('update:reviewerSearch', value),
})

const selectedReviewerExternalUserIdModel = computed({
  get: () => props.selectedReviewerExternalUserId,
  set: value => emit('update:selectedReviewerExternalUserId', value),
})

const selectedReviewerCandidate = computed(() =>
  props.reviewerCandidates.find(candidate => candidate.externalUserId === props.selectedReviewerExternalUserId) ?? null,
)
</script>

<style scoped>
.provider-detail-panel {
  border: 1px solid var(--color-border);
  border-radius: 16px;
  padding: 1rem;
  background: var(--color-surface);
}

.provider-detail-panel-header,
.provider-candidate-item {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 0.75rem;
}

.provider-detail-panel-header h4,
.provider-candidate-item strong,
.provider-reviewer-current strong {
  margin: 0;
}

.provider-candidate-item p,
.provider-reviewer-current p {
  margin: 0.25rem 0 0;
}

.provider-form-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 0.85rem;
}

.provider-form-grid-full {
  grid-column: 1 / -1;
}

.provider-form-grid-tight {
  margin-top: 0.75rem;
}

.provider-candidate-list {
  list-style: none;
  padding: 0;
  margin: 1rem 0 0;
  display: grid;
  gap: 0.65rem;
}

.provider-candidate-item {
  padding: 0.8rem 0.9rem;
  border-radius: 14px;
  background: var(--color-bg);
  border: 1px solid var(--color-border);
  cursor: pointer;
}

.provider-candidate-item.selected {
  border-color: var(--color-accent);
  background: rgba(34, 211, 238, 0.08);
}

.provider-reviewer-current {
  padding: 0.8rem 0.9rem;
  border-radius: 14px;
  background: rgba(34, 197, 94, 0.08);
  border: 1px solid var(--color-success);
  margin-bottom: 0.9rem;
}
</style>
