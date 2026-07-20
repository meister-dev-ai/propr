<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div v-if="client" class="client-budget-tab">
        <div class="section-card">
            <div class="section-card-header">
                <h3>Budget</h3>
            </div>
            <div class="section-card-body section-card-body--compact">
                <p class="muted budget-intro">
                    Optional USD spend caps. A <strong>soft cap</strong> holds new reviews once the scope's spend
                    reaches it (running reviews still finish); a <strong>hard cap</strong> cuts further model calls
                    mid-review and publishes the findings produced so far. Leave a field blank for no limit. Caps
                    compose most-restrictively across scopes, and a held or stopped review is resumed by restarting it.
                </p>
                <p v-if="!isBudgetingAvailable && budgetingUpgradeMessage" class="muted budget-upgrade">
                    {{ budgetingUpgradeMessage }}
                </p>
                <fieldset class="budget-grid" :disabled="!isBudgetingAvailable">
                    <div class="form-field">
                        <label for="monthlyBudgetSoftCapUsd">Monthly soft cap (USD)</label>
                        <input id="monthlyBudgetSoftCapUsd" v-model="editedMonthlyBudgetSoftCapUsd"
                            name="monthlyBudgetSoftCapUsd" type="number" min="0" step="0.01" placeholder="No limit" />
                    </div>
                    <div class="form-field">
                        <label for="monthlyBudgetHardCapUsd">Monthly hard cap (USD)</label>
                        <input id="monthlyBudgetHardCapUsd" v-model="editedMonthlyBudgetHardCapUsd"
                            name="monthlyBudgetHardCapUsd" type="number" min="0" step="0.01" placeholder="No limit" />
                    </div>
                    <div class="form-field">
                        <label for="pullRequestBudgetSoftCapUsd">Per-PR soft cap (USD)</label>
                        <input id="pullRequestBudgetSoftCapUsd" v-model="editedPullRequestBudgetSoftCapUsd"
                            name="pullRequestBudgetSoftCapUsd" type="number" min="0" step="0.01" placeholder="No limit" />
                    </div>
                    <div class="form-field">
                        <label for="pullRequestBudgetHardCapUsd">Per-PR hard cap (USD)</label>
                        <input id="pullRequestBudgetHardCapUsd" v-model="editedPullRequestBudgetHardCapUsd"
                            name="pullRequestBudgetHardCapUsd" type="number" min="0" step="0.01" placeholder="No limit" />
                    </div>
                    <div class="form-field">
                        <label for="incrementBudgetHardCapUsd">Per-increment hard cap (USD)</label>
                        <input id="incrementBudgetHardCapUsd" v-model="editedIncrementBudgetHardCapUsd"
                            name="incrementBudgetHardCapUsd" type="number" min="0" step="0.01" placeholder="No limit" />
                        <p class="muted budget-field-note">A single increment is one review job, so it is capped by a hard limit only.</p>
                    </div>
                </fieldset>
                <div class="budget-actions">
                    <button :disabled="!isBudgetingAvailable || !isBudgetButtonEnabled()"
                        class="btn-primary inline-save-btn budget-save-btn"
                        @click="saveBudgetConfig">
                        Save
                    </button>
                </div>
                <span v-if="saveError" class="error">{{ saveError }}</span>
            </div>
        </div>
    </div>
</template>

<script lang="ts" setup>
import { inject } from "vue";
import { ClientDetailVmKey } from "@/features/clients/view-models/useClientDetailViewModel";

const vm = inject(ClientDetailVmKey)!;
const {
    client,
    saveError,
    editedMonthlyBudgetSoftCapUsd,
    editedMonthlyBudgetHardCapUsd,
    editedPullRequestBudgetSoftCapUsd,
    editedPullRequestBudgetHardCapUsd,
    editedIncrementBudgetHardCapUsd,
    saveBudgetConfig,
    isBudgetButtonEnabled,
    isBudgetingAvailable,
    budgetingUpgradeMessage,
} = vm;
</script>

<style scoped>
.section-card-body--compact {
    padding: 1rem 1.25rem;
}

.inline-save-btn {
    flex-shrink: 0;
    align-self: flex-end;
    margin-bottom: 0;
}

.muted {
    color: var(--color-text-muted);
    font-style: italic;
    padding: 1rem 1.25rem;
}

.budget-intro {
    padding: 0 0 0.85rem;
}

.budget-grid {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 0.85rem 1rem;
    /* Reset the fieldset defaults so the wrapper keeps behaving as a plain grid. */
    border: 0;
    margin: 0;
    padding: 0;
    min-inline-size: 0;
}

.budget-upgrade {
    padding: 0 0 0.6rem;
    color: var(--color-warning);
    font-style: normal;
}

.budget-field-note {
    padding: 0.3rem 0 0;
    font-size: 0.8rem;
}

.budget-actions {
    display: flex;
    justify-content: flex-end;
    margin-top: 0.85rem;
}
</style>
