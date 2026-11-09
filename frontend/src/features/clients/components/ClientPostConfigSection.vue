<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div v-if="client" class="section-card">
        <div class="section-card-header">
            <h3>Post configuration</h3>
        </div>
        <div class="section-card-body section-card-body--compact">
            <div class="inline-field-row review-publication-row">
                <div class="form-field flex-1 review-publication-field">
                    <label for="findingBadgeStyle">Finding badges</label>
                    <select id="findingBadgeStyle" v-model="editedFindingBadgeStyle" name="findingBadgeStyle"
                        aria-describedby="findingBadgeStyleHelp">
                        <option value="shieldsIo">Image badges (Shields.io)</option>
                        <option value="plain">Plain gray badges</option>
                    </select>
                    <p id="findingBadgeStyleHelp" class="muted review-publication-copy">
                        Image badges load from Shields.io. Plain gray badges use inline code without external images.
                        This setting applies to finding comments on every SCM provider.
                    </p>
                </div>
            </div>
            <div class="inline-field-row review-publication-row">
                <div class="form-field flex-1 review-publication-field">
                    <label for="minimumSeverityToPost">Minimum classification to post</label>
                    <select id="minimumSeverityToPost" v-model="editedMinimumSeverityToPost"
                        name="minimumSeverityToPost">
                        <option v-for="option in MIN_SEVERITY_OPTIONS" :key="option.value" :value="option.value">
                            {{ option.label }}
                        </option>
                    </select>
                    <p class="muted review-publication-copy">
                        Findings below this classification are not posted to the pull request. They stay visible
                        in the ProPR review. Classification order: Error, Warning, Suggestion, Info. The risk of a
                        finding has its own setting below.
                    </p>
                </div>
            </div>

            <div class="inline-field-row review-publication-row">
                <div class="form-field flex-1 review-publication-field">
                    <label for="minimumRiskSeverityToPost">Minimum risk to post</label>
                    <select id="minimumRiskSeverityToPost" v-model="editedMinimumRiskSeverityToPost"
                        name="minimumRiskSeverityToPost">
                        <option v-for="option in MIN_RISK_OPTIONS" :key="option.value" :value="option.value">
                            {{ option.label }}
                        </option>
                    </select>
                    <p class="muted review-publication-copy">
                        Findings with an assessed risk below this level are not posted to the pull request. They
                        stay visible in the ProPR review. Risk order: Critical, High, Medium, Low. The risk follows
                        from the impact and the likelihood of a finding. A finding without a complete risk
                        assessment is posted.
                    </p>
                </div>
            </div>

            <div class="inline-field-row review-publication-row">
                <div class="form-field flex-1 review-publication-field">
                    <span class="post-config-group-label">Finding types to post</span>
                    <div class="finding-type-options">
                        <label v-for="option in FINDING_TYPE_OPTIONS" :key="option.slug"
                            class="checkbox-field" :for="`postFindingType-${option.slug}`">
                            <input :id="`postFindingType-${option.slug}`"
                                :checked="!editedWithheldFindingTypes.includes(option.slug)"
                                :value="option.slug" name="postFindingTypes" type="checkbox"
                                @change="toggleFindingType(option.slug, ($event.target as HTMLInputElement).checked)" />
                            <strong>{{ option.label }}</strong>
                        </label>
                    </div>
                    <p class="muted review-publication-copy">
                        Findings of the cleared types are not posted to the pull request. They stay visible in the
                        ProPR review. A finding with several types is posted when at least one of its types is
                        selected. A finding without a type is posted.
                    </p>
                </div>
            </div>

            <div class="inline-field-row review-publication-row">
                <div class="form-field flex-1 review-publication-field">
                    <span class="post-config-group-label">Auto-resolve classifications</span>
                    <label v-for="option in SEVERITY_OPTIONS" :key="option.value"
                        class="checkbox-field auto-resolve-option" :for="`autoResolve-${option.value}`">
                        <input :id="`autoResolve-${option.value}`" v-model="editedAutoResolveSeverities"
                            :value="option.value" name="autoResolveSeverities" type="checkbox" />
                        <strong>{{ option.label }}</strong>
                    </label>
                    <p class="muted review-publication-copy">
                        Comments of the selected classifications are posted and then immediately resolved, with
                        a note that they were auto-resolved by ProPR.
                    </p>
                </div>
            </div>

            <div class="inline-field-row review-publication-row">
                <div class="form-field flex-1 review-publication-field">
                    <label class="checkbox-field" for="withholdOutOfScopeFindings">
                        <input id="withholdOutOfScopeFindings" v-model="editedWithholdOutOfScopeFindings"
                            name="withholdOutOfScopeFindings" type="checkbox" />
                        <strong>Do not post findings outside the changed lines</strong>
                    </label>
                    <p class="muted review-publication-copy">
                        A review reads whole files, so it can find something in pre-existing code far from the
                        lines a pull request changed. Those findings stay in the ProPR review and the pull request
                        summary reports how many were held back.
                    </p>
                </div>
                <button :disabled="!isPostConfigButtonEnabled()"
                    class="btn-primary inline-save-btn post-config-save-btn" @click="savePostConfiguration">
                    Save
                </button>
            </div>
            <span v-if="saveError" class="error">{{ saveError }}</span>
        </div>
    </div>
</template>

<script lang="ts" setup>
import { inject } from "vue";
import {
    ClientDetailVmKey,
    type CommentSeverity,
    type FindingRiskSeverity,
} from "@/features/clients/view-models/useClientDetailViewModel";

const vm = inject(ClientDetailVmKey)!;
const {
    client,
    saveError,
    editedFindingBadgeStyle,
    editedMinimumSeverityToPost,
    editedAutoResolveSeverities,
    editedWithholdOutOfScopeFindings,
    editedMinimumRiskSeverityToPost,
    editedWithheldFindingTypes,
    savePostConfiguration,
    isPostConfigButtonEnabled,
} = vm;

// Minimum-severity threshold options, least to most restrictive. 'info' (the lowest rank) posts everything.
const MIN_SEVERITY_OPTIONS: { value: CommentSeverity; label: string }[] = [
    { value: "info", label: "Info — post everything (default)" },
    { value: "suggestion", label: "Suggestion and above" },
    { value: "warning", label: "Warning and above" },
    { value: "error", label: "Error only" },
];

// Minimum-risk options, least to most restrictive. 'low' posts every finding.
const MIN_RISK_OPTIONS: { value: FindingRiskSeverity; label: string }[] = [
    { value: "low", label: "Low — post everything (default)" },
    { value: "medium", label: "Medium and above" },
    { value: "high", label: "High and above" },
    { value: "critical", label: "Critical only" },
];

// The core finding types of the review taxonomy, in the order the taxonomy lists them. The server accepts these
// slugs only.
const FINDING_TYPE_OPTIONS: { slug: string; label: string }[] = [
    { slug: "logic-error", label: "Logic error" },
    { slug: "data-validation", label: "Data validation" },
    { slug: "resource-handling", label: "Resource handling" },
    { slug: "concurrency", label: "Concurrency" },
    { slug: "api-contract", label: "API contract" },
    { slug: "security", label: "Security" },
    { slug: "performance", label: "Performance" },
    { slug: "error-handling-observability", label: "Error handling and observability" },
    { slug: "design-structure", label: "Design and structure" },
    { slug: "naming-clarity", label: "Naming and clarity" },
    { slug: "documentation-tests", label: "Documentation and tests" },
];

// The setting stores the types that are not posted, so a type added to the taxonomy later is posted by default.
function toggleFindingType(slug: string, posted: boolean): void {
    const withheld = editedWithheldFindingTypes.value.filter((value) => value !== slug);
    editedWithheldFindingTypes.value = posted ? withheld : [...withheld, slug];
}

// Severities offered for auto-resolution, highest to lowest.
const SEVERITY_OPTIONS: { value: CommentSeverity; label: string }[] = [
    { value: "error", label: "Error" },
    { value: "warning", label: "Warning" },
    { value: "suggestion", label: "Suggestion" },
    { value: "info", label: "Info" },
];
</script>

<style scoped>
.section-card-body--compact {
    padding: 1rem 1.25rem;
}

.inline-field-row {
    display: flex;
    align-items: flex-end;
    gap: 0.75rem;
}

.flex-1 {
    flex: 1;
}

.inline-save-btn {
    flex-shrink: 0;
    align-self: flex-end;
    margin-bottom: 0;
}

.post-config-group-label {
    display: block;
    font-weight: 500;
    margin-bottom: 0.35rem;
}

.checkbox-field {
    display: flex;
    align-items: center;
    gap: 0.55rem;
}

.auto-resolve-option {
    margin-top: 0.25rem;
}

.finding-type-options {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(15rem, 1fr));
    gap: 0.35rem 1rem;
}

.muted {
    color: var(--color-text-muted);
    font-style: italic;
    padding: 1rem 1.25rem;
}
</style>
