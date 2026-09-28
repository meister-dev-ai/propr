<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
    <div v-if="client" class="client-budget-tab">
        <div class="section-card">
            <div class="section-card-header">
                <h3>Budget</h3>
            </div>
            <div class="section-card-body section-card-body--compact">
                <p class="muted budget-intro">
                    Optional USD spend caps. Monthly and per-PR <strong>soft caps</strong> hold new reviews once the
                    scope's spend reaches them (running reviews still finish); the per-increment <strong>soft cap</strong>
                    instead stops a running review from scanning more files and finishes it with a synthesis. A
                    <strong>hard cap</strong> cuts further model calls mid-review and publishes the findings produced so
                    far. Leave a field blank for no limit. Caps compose most-restrictively across scopes, and a held or
                    stopped review is resumed by restarting it.
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
                        <label for="incrementBudgetSoftCapUsd">Per-increment soft cap (USD)</label>
                        <input id="incrementBudgetSoftCapUsd" v-model="editedIncrementBudgetSoftCapUsd"
                            name="incrementBudgetSoftCapUsd" type="number" min="0" step="0.01" placeholder="No limit" />
                        <p class="muted budget-field-note">When a single review reaches this, it stops scanning further files and finishes with a synthesis noting the review was soft-capped.</p>
                    </div>
                    <div class="form-field">
                        <label for="incrementBudgetHardCapUsd">Per-increment hard cap (USD)</label>
                        <input id="incrementBudgetHardCapUsd" v-model="editedIncrementBudgetHardCapUsd"
                            name="incrementBudgetHardCapUsd" type="number" min="0" step="0.01" placeholder="No limit" />
                        <p class="muted budget-field-note">A single increment is one review job; reaching this cuts further model calls mid-review.</p>
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

        <div class="section-card">
            <div class="section-card-header">
                <h3>Review limits</h3>
            </div>
            <div class="section-card-body section-card-body--compact">
                <p class="muted budget-intro">
                    Optional bounds on the size of a review. A pull request measured past one of the first three
                    bounds is not reviewed: the job ends before any model call and ProPR posts the measured value,
                    the bound and the way forward on the pull request. The reviews-per-hour bound holds a job
                    instead, and it starts by itself once the hour has passed. Leave a field blank for no bound.
                    Changed files are counted after exclusions and after files carried forward from an earlier
                    review. These bounds protect the installation, so they need no licence.
                </p>
                <fieldset class="budget-grid">
                    <legend class="budget-legend">Review admission limits</legend>
                    <div class="form-field">
                        <label for="admissionMaxChangedFiles">Changed files per review</label>
                        <input id="admissionMaxChangedFiles" v-model="editedAdmissionMaxChangedFiles"
                            aria-describedby="admissionPolicyError" data-testid="admission-max-changed-files"
                            name="admissionMaxChangedFiles" type="number" min="1" max="2147483647" step="1"
                            placeholder="No limit" />
                    </div>
                    <div class="form-field">
                        <label for="admissionMaxChangedLines">Changed lines per review</label>
                        <input id="admissionMaxChangedLines" v-model="editedAdmissionMaxChangedLines"
                            aria-describedby="admissionPolicyError" data-testid="admission-max-changed-lines"
                            name="admissionMaxChangedLines" type="number" min="1" max="2147483647" step="1"
                            placeholder="No limit" />
                    </div>
                    <div class="form-field">
                        <label for="admissionMaxDiffBytes">Diff size per review (bytes)</label>
                        <input id="admissionMaxDiffBytes" v-model="editedAdmissionMaxDiffBytes"
                            aria-describedby="admissionPolicyError" data-testid="admission-max-diff-bytes"
                            name="admissionMaxDiffBytes" type="number" min="1" max="2147483647" step="1"
                            placeholder="No limit" />
                        <p class="muted budget-field-note">The size of the unified diff as ProPR obtains it, from the SCM host or from the mirror the review runs against. Each provider renders a diff in its own way, so the measurement is an approximation.</p>
                    </div>
                    <div class="form-field">
                        <label for="admissionMaxReviewsPerPullRequestPerHour">Reviews per pull request per hour</label>
                        <input id="admissionMaxReviewsPerPullRequestPerHour"
                            v-model="editedAdmissionMaxReviewsPerPullRequestPerHour"
                            aria-describedby="admissionPolicyError" data-testid="admission-max-reviews-per-hour"
                            name="admissionMaxReviewsPerPullRequestPerHour" type="number" min="1" max="2147483647"
                            step="1" placeholder="No limit" />
                        <p class="muted budget-field-note">ProPR counts the reviews of the pull request it ran in the past hour: a review that is running, and a review that made at least one model call. A push burst past this bound waits for the hour to pass and then runs. No comment is posted for a wait.</p>
                    </div>
                    <div class="form-field">
                        <label for="admissionMaxRepositoryMegabytes">Repository size (MB)</label>
                        <input id="admissionMaxRepositoryMegabytes" v-model="editedAdmissionMaxRepositoryMegabytes"
                            aria-describedby="admissionPolicyError" data-testid="admission-max-repository-megabytes"
                            name="admissionMaxRepositoryMegabytes" type="number" min="1" max="2147483647" step="1"
                            placeholder="No limit" />
                        <p class="muted budget-field-note">Applied while the repository is fetched and checked out. ProPR stops the transfer once it passes this size and does not review the repository. No size is stored between reviews.</p>
                    </div>
                </fieldset>
                <p v-if="admissionPolicyError" id="admissionPolicyError" class="error" role="alert"
                    data-testid="admission-validation">{{ admissionPolicyError }}</p>
                <div class="budget-actions">
                    <button :disabled="!isAdmissionButtonEnabled()"
                        class="btn-primary inline-save-btn budget-save-btn"
                        data-testid="admission-save"
                        @click="saveAdmissionPolicy">
                        Save
                    </button>
                </div>
                <!-- Only the admission save writes this error, so a message here came from the Save above it.
                     A failed budget save is reported in the Budget card with its own fields. -->
                <span v-if="admissionSaveError" class="error" role="alert" data-testid="admission-save-error">{{ admissionSaveError }}</span>
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
    admissionSaveError,
    editedMonthlyBudgetSoftCapUsd,
    editedMonthlyBudgetHardCapUsd,
    editedPullRequestBudgetSoftCapUsd,
    editedPullRequestBudgetHardCapUsd,
    editedIncrementBudgetSoftCapUsd,
    editedIncrementBudgetHardCapUsd,
    editedAdmissionMaxChangedFiles,
    editedAdmissionMaxChangedLines,
    editedAdmissionMaxDiffBytes,
    editedAdmissionMaxReviewsPerPullRequestPerHour,
    editedAdmissionMaxRepositoryMegabytes,
    saveBudgetConfig,
    saveAdmissionPolicy,
    isBudgetButtonEnabled,
    isAdmissionButtonEnabled,
    admissionPolicyError,
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

.budget-legend {
    padding: 0 0 0.35rem;
    color: var(--color-text-muted);
    font-size: 0.85rem;
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
