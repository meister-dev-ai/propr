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
                    <span id="findingTypeDestinationsLabel" class="post-config-group-label">
                        Destination of finding types
                    </span>
                    <div class="finding-type-destinations" role="group"
                        aria-labelledby="findingTypeDestinationsLabel" aria-describedby="findingTypeDestinationsHelp">
                        <div v-for="row in FINDING_TYPE_DESTINATION_ROWS" :key="row.key"
                            class="finding-type-destination">
                            <label :for="destinationFieldId(row.key)">{{ row.label }}</label>
                            <select :id="destinationFieldId(row.key)" :name="destinationFieldId(row.key)"
                                :value="editedFindingTypeDestinations[row.key]"
                                @change="setDestination(row.key, ($event.target as HTMLSelectElement).value)">
                                <option v-for="option in FINDING_POST_DESTINATION_OPTIONS" :key="option.value"
                                    :value="option.value">
                                    {{ option.label }}
                                </option>
                            </select>
                        </div>
                    </div>
                    <div v-if="qualifiedNotes.length > 0" id="findingTypeQualifiedDestinations"
                        class="finding-type-qualified-destinations">
                        <p class="muted review-publication-copy">
                            These destinations for findings with a qualifier were set through the API, and for those
                            findings they take precedence over the row of their type.
                        </p>
                        <ul>
                            <li v-for="note in qualifiedNotes" :key="note.key">
                                {{ note.typeLabel }}, qualifier {{ note.qualifier }}: {{ note.destinationLabel }}
                            </li>
                        </ul>
                    </div>
                    <p id="findingTypeDestinationsHelp" class="muted review-publication-copy">
                        A comment at the line is posted to the pull request. A finding listed in the summary is not
                        posted as a comment, and the review summary on the pull request lists it. The summary lists up
                        to 15 of these findings and gives the number of the others. A finding kept in
                        the ProPR review only is neither posted nor listed, and the summary states how many were kept.
                        Every finding stays visible in the ProPR review. A finding with several types takes the most
                        visible destination of its types, and a finding without a type is posted as a comment.
                        "Missing tests or documentation" applies to findings that only ask for a test or a document
                        that does not exist yet. "Incorrect tests or documentation" applies to every other finding
                        of the type Documentation and tests.
                    </p>
                </div>
            </div>

            <div class="inline-field-row review-publication-row">
                <div class="form-field flex-1 review-publication-field">
                    <label for="maximumFindingsToPost">Maximum findings to post</label>
                    <input id="maximumFindingsToPost" v-model="editedMaximumFindingsToPost" name="maximumFindingsToPost"
                        type="number" min="1" step="1" placeholder="No limit" class="maximum-findings-input"
                        aria-describedby="maximumFindingsToPostHelp maximumFindingsToPostError" />
                    <p id="maximumFindingsToPostHelp" class="muted review-publication-copy">
                        ProPR posts at most this number of findings as comments on one pull request. When more
                        findings remain after the settings above, ProPR posts the highest ranked ones and keeps the
                        others in the ProPR review, and the summary states how many were held back. The rank compares
                        the classification first, then the likelihood, then the number of places a comment stands for,
                        and then the risk. Findings listed in the summary do not count towards the limit. Leave the
                        field empty to post every finding.
                    </p>
                    <p v-if="maximumFindingsToPostError" id="maximumFindingsToPostError" class="error" role="alert">
                        {{ maximumFindingsToPostError }}
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
import { computed, inject } from "vue";
import {
    ClientDetailVmKey,
    type CommentSeverity,
    type FindingPostDestination,
    type FindingRiskSeverity,
} from "@/features/clients/view-models/useClientDetailViewModel";
import {
    FINDING_POST_DESTINATION_OPTIONS,
    FINDING_TYPE_DESTINATION_ROWS,
    qualifiedDestinationNotes,
} from "@/features/clients/view-models/findingTypeDestinations";

const vm = inject(ClientDetailVmKey)!;
const {
    client,
    saveError,
    editedFindingBadgeStyle,
    editedMinimumSeverityToPost,
    editedAutoResolveSeverities,
    editedWithholdOutOfScopeFindings,
    editedMinimumRiskSeverityToPost,
    editedFindingTypeDestinations,
    editedMaximumFindingsToPost,
    maximumFindingsToPostError,
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

// Keys with a qualifier that no row shows stay as they are when the rows are saved, so they are read from the stored map.
const qualifiedNotes = computed(() => qualifiedDestinationNotes(client.value?.findingTypeDestinations));

// A colon in an element id has to be escaped in a CSS selector, so the id writes the colon of a key as a hyphen.
function destinationFieldId(key: string): string {
    return `findingTypeDestination-${key.replace(":", "-")}`;
}

// The edited map is replaced, not changed in place, so that code watching the reference sees every change.
function setDestination(key: string, destination: string): void {
    editedFindingTypeDestinations.value = {
        ...editedFindingTypeDestinations.value,
        [key]: destination as FindingPostDestination,
    };
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

.finding-type-destinations {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(min(100%, 22rem), 1fr));
    gap: 0.5rem 1.5rem;
}

.finding-type-destination {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.75rem;
}

.finding-type-destination select {
    flex: 0 0 12rem;
}

.finding-type-qualified-destinations ul {
    margin: 0 1.25rem;
    padding-left: 1.25rem;
}

.maximum-findings-input {
    max-width: 12rem;
}

.muted {
    color: var(--color-text-muted);
    font-style: italic;
    padding: 1rem 1.25rem;
}
</style>
