<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<script setup lang="ts">
/**
 * The two limits on how the reviews of a tenant run: how many run at once, and how long one review may run.
 * Every viewer of the tenant sees them. Only a platform administrator can change them, because the endpoint
 * that stores them is reserved to platform administrators.
 */

import { computed, onMounted, watch } from 'vue'

import { useSession } from '@/composables/useSession'
import {
  formatConcurrentReviews,
  formatReviewDuration,
  useTenantReviewLimits,
} from '@/features/tenants/view-models/useTenantReviewLimits'

interface Props {
  tenantId: string
}

const props = defineProps<Props>()

const { isAdmin } = useSession()
const limits = useTenantReviewLimits(() => props.tenantId)

const concurrentReadout = computed(() => formatConcurrentReviews(limits.limits.value?.maxConcurrentReviews))
const durationReadout = computed(() => formatReviewDuration(limits.limits.value?.maxReviewDurationSeconds))

onMounted(() => {
  void limits.loadLimits()
})

// The tenant route reuses this component across tenants, so a navigation changes the prop without remounting.
watch(
  () => props.tenantId,
  () => {
    void limits.loadLimits()
  },
)
</script>

<template>
  <section class="section-card tenant-review-limits" data-testid="tenant-review-limits">
    <div class="section-card-header">
      <div>
        <h2>Review limits</h2>
        <p class="section-subtitle">
          How many reviews of this tenant run at the same time, and how long one review may run. The limits of the
          installation apply as well.
        </p>
      </div>
    </div>

    <div class="section-card-body">
      <p v-if="limits.error.value" class="error" data-testid="tenant-review-limits-error">{{ limits.error.value }}</p>

      <p v-if="limits.loading.value" class="muted-hint">Loading review limits…</p>

      <!-- Without a successful read the stored limits are unknown, and blank fields would read as "no limit". -->
      <p v-else-if="limits.limits.value === null" class="muted-hint" data-testid="tenant-review-limits-unavailable">
        The review limits were not read, so they are not shown and cannot be changed.
        <button
          type="button"
          class="btn-secondary btn-sm"
          data-testid="tenant-review-limits-retry"
          @click="limits.loadLimits()"
        >
          Try again
        </button>
      </p>

      <template v-else-if="isAdmin">
        <fieldset class="review-limits-grid" :disabled="limits.saving.value">
          <div class="form-field">
            <label for="tenantMaxConcurrentReviews">Concurrent reviews</label>
            <input
              id="tenantMaxConcurrentReviews"
              v-model="limits.editedConcurrentReviews.value"
              data-testid="tenant-max-concurrent-reviews"
              name="tenantMaxConcurrentReviews"
              type="number"
              min="1"
              step="1"
              placeholder="No limit"
            />
          </div>
          <div class="form-field">
            <label for="tenantMaxReviewDurationMinutes">Maximum review duration (minutes)</label>
            <input
              id="tenantMaxReviewDurationMinutes"
              v-model="limits.editedDurationMinutes.value"
              data-testid="tenant-max-review-duration"
              name="tenantMaxReviewDurationMinutes"
              type="number"
              min="1"
              max="1440"
              step="any"
              placeholder="No limit"
            />
          </div>
        </fieldset>

        <p v-if="limits.validationError.value" class="error" data-testid="tenant-review-limits-validation">
          {{ limits.validationError.value }}
        </p>

        <div class="review-limits-actions">
          <span v-if="limits.saved.value" class="muted" data-testid="tenant-review-limits-saved">
            Review limits saved. Reviews that start from now on follow them, and running reviews are not stopped.
          </span>
          <button
            type="button"
            class="btn-primary btn-sm"
            data-testid="tenant-review-limits-save"
            :disabled="!limits.isSaveEnabled()"
            @click="limits.saveLimits()"
          >
            {{ limits.saving.value ? 'Saving…' : 'Save review limits' }}
          </button>
        </div>
      </template>

      <template v-else>
        <dl class="review-limits-readout">
          <div>
            <dt>Concurrent reviews</dt>
            <dd data-testid="tenant-max-concurrent-reviews-readout">{{ concurrentReadout }}</dd>
          </div>
          <div>
            <dt>Maximum review duration</dt>
            <dd data-testid="tenant-max-review-duration-readout">{{ durationReadout }}</dd>
          </div>
        </dl>
        <p class="muted-hint" data-testid="tenant-review-limits-readonly">
          Only a platform administrator can change these limits.
        </p>
      </template>

      <p v-if="limits.limits.value !== null" class="muted review-limits-note">
        Reviews beyond the concurrent limit wait in the queue until a running review of this tenant finishes. A
        review that reaches the maximum duration starts no further files, completes with the files it has reviewed,
        and lists the files it did not review in its summary.
      </p>
    </div>
  </section>
</template>

<style scoped>
.review-limits-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 0.85rem 1rem;
  /* Reset the fieldset defaults so the wrapper keeps behaving as a plain grid. */
  border: 0;
  margin: 0;
  padding: 0;
  min-inline-size: 0;
}

.review-limits-readout {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 0.85rem 1rem;
  margin: 0;
}

.review-limits-readout dt {
  color: var(--color-text-muted);
  font-size: 0.85rem;
}

.review-limits-readout dd {
  margin: 0.15rem 0 0;
  font-variant-numeric: tabular-nums;
}

.review-limits-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 0.75rem;
  margin-top: 1rem;
}

.review-limits-note {
  margin-block: 0.75rem 0;
  font-size: 0.85rem;
}

@media (max-width: 640px) {
  .review-limits-grid,
  .review-limits-readout {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
