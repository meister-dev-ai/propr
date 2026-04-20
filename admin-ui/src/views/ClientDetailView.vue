<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="page-with-sidebar">
        <!-- Sidebar Navigation -->
        <aside class="page-sidebar">
            <RouterLink class="back-link" to="/clients" style="margin-bottom: 0;">
                <i class="fi fi-rr-arrow-left"></i> Back to clients
            </RouterLink>

            <div v-if="!notFound && !loading && client" class="detail-page-title" style="margin-bottom: 0;">
                <h2 style="font-size: 1.25rem;">{{ client.displayName }}</h2>
                <p class="detail-page-subtitle">Client Configuration</p>
            </div>

            <div v-if="!notFound && !loading && client" class="sidebar-nav">
                <div class="sidebar-nav-group">
                    <h4>Configuration</h4>
                    <button class="sidebar-nav-link" :class="{ 'active': activeTab === 'config' }" @click="activeTab = 'config'">
                        <i class="fi fi-rr-settings"></i> System
                    </button>
                    <button class="sidebar-nav-link" :class="{ 'active': activeTab === 'crawl-configs' }" @click="activeTab = 'crawl-configs'">
                        <i class="fi fi-rr-spider"></i> Crawl Configs
                    </button>
                    <button class="sidebar-nav-link" :class="{ 'active': activeTab === 'webhooks' }" @click="activeTab = 'webhooks'">
                        <i class="fi fi-rr-link-alt"></i> Webhooks
                    </button>
                    <button class="sidebar-nav-link" :class="{ 'active': activeTab === 'providers' }" @click="activeTab = 'providers'">
                        <i class="fi fi-rr-plug-connection"></i> Providers
                    </button>
                    <button class="sidebar-nav-link" :class="{ 'active': activeTab === 'procursor' }" @click="activeTab = 'procursor'">
                        <i class="fi fi-rr-books"></i> ProCursor
                    </button>
                    <button class="sidebar-nav-link" :class="{ 'active': activeTab === 'ai' }" @click="activeTab = 'ai'; loadAiConnections()">
                        <i class="fi fi-rr-robot"></i> AI Connections
                    </button>
                </div>

                <div class="sidebar-nav-group">
                    <h4>Reviews & Overrides</h4>
                    <button class="sidebar-nav-link" :class="{ 'active': activeTab === 'history' }" @click="activeTab = 'history'">
                        <i class="fi fi-rr-time-past"></i> Review History
                    </button>
                    <button class="sidebar-nav-link" :class="{ 'active': activeTab === 'dismissals' }" @click="activeTab = 'dismissals'; loadDismissals()">
                        <i class="fi fi-rr-ban"></i> Dismissed Findings
                    </button>
                    <button class="sidebar-nav-link" :class="{ 'active': activeTab === 'prompt-overrides' }" @click="activeTab = 'prompt-overrides'; loadPromptOverrides()">
                        <i class="fi fi-rr-code-simple"></i> Prompt Overrides
                    </button>
                </div>

                <div v-if="isProCursorTokenUsageReportingEnabled" class="sidebar-nav-group">
                    <h4>Analytics</h4>
                    <button class="sidebar-nav-link" :class="{ 'active': activeTab === 'usage' }" @click="activeTab = 'usage'">
                        <i class="fi fi-rr-chart-histogram"></i> Tokens & Usage
                    </button>
                </div>
            </div>
        </aside>

        <!-- Main Content Area -->
        <main class="page-main-content">
            <p v-if="notFound" class="error" style="padding-top: 1rem;">Client not found.</p>
            <p v-else-if="loading" class="loading" style="padding-top: 1rem;">Loading…</p>

            <template v-else-if="client">

            <!-- Tab: Configuration -->
            <div v-show="activeTab === 'config'">

                <!-- Section 1: Client Identity -->
                <div class="section-card">
                    <div class="section-card-header">
                        <h3>Client Identity</h3>
                        <div class="section-card-header-actions">
                            <span :class="client.isActive ? 'chip chip-success' : 'chip chip-muted'">
                                <i :class="client.isActive ? 'fi fi-rr-check-circle' : 'fi fi-rr-ban'"></i>
                                {{ client.isActive ? 'Active' : 'Inactive' }}
                            </span>
                            <button
                                :disabled="saving"
                                :class="client.isActive ? 'btn-danger toggle-status-btn' : 'btn-primary toggle-status-btn'"
                                @click="toggleStatus"
                            >
                                {{ client.isActive ? 'Disable' : 'Enable' }}
                            </button>
                        </div>
                    </div>
                    <div class="section-card-body section-card-body--compact">
                        <div class="inline-field-row">
                            <div class="form-field flex-1">
                                <label for="displayName">Display Name</label>
                                <input id="displayName" v-model="editedDisplayName" name="displayName" type="text" />
                            </div>
                            <button :disabled="saving" class="btn-primary inline-save-btn save-btn" @click="saveDisplayName">Save</button>
                        </div>
                        <span v-if="saveError" class="error">{{ saveError }}</span>
                    </div>
                </div>

                <div class="section-card">
                    <div class="section-card-header">
                        <h3>Provider Access</h3>
                    </div>
                    <div class="section-card-body section-card-body--compact">
                        <p class="muted">
                            Provider connections, organization scopes, and reviewer identities are managed in the Providers tab.
                        </p>
                    </div>
                </div>

                <!-- Danger Zone -->
                <div class="danger-zone-card">
                    <div class="danger-zone-info">
                        <i class="fi fi-rr-triangle-warning"></i>
                        <div>
                            <h3>Danger Zone</h3>
                            <p>Deleting this client is permanent and cannot be undone.</p>
                        </div>
                    </div>
                    <button class="btn-danger" @click="showDeleteDialog = true">
                        <i class="fi fi-rr-trash"></i> Delete Client
                    </button>
                    <ConfirmDialog
                        :open="showDeleteDialog"
                        message="Delete this client permanently?"
                        @cancel="showDeleteDialog = false"
                        @confirm="handleDelete"
                    />
                </div>
            </div>

            <div v-show="activeTab === 'crawl-configs'">
                <ClientCrawlConfigsTab :clientId="client.id" />
            </div>

            <div v-show="activeTab === 'webhooks'">
                <ClientWebhookConfigsTab :clientId="client.id" />
            </div>

            <div v-show="activeTab === 'providers'" class="provider-operations-tab">
                <ClientProviderConnectionsTab :clientId="client.id" />

                <div v-if="activeTab === 'providers'" class="provider-operations-stack">
                    <ProviderConnectionStatusList :clientId="client.id" />
                    <ProviderConnectionAuditTrail :clientId="client.id" />
                </div>
            </div>

            <div v-show="activeTab === 'procursor'">
                <ClientProCursorTab :clientId="client.id" />
            </div>

            <!-- Tab: Usage -->
            <div v-if="isProCursorTokenUsageReportingEnabled" v-show="activeTab === 'usage'">
                <UsageDashboard :clientId="client.id" />
            </div>

            <!-- Tab: Review History -->
            <div v-show="activeTab === 'history'">
                <ReviewHistorySection :clientId="client.id" />
            </div>

            <!-- Tab: Dismissed Findings -->
            <div v-show="activeTab === 'dismissals'">
                <div class="section-card">
                    <div class="section-card-header">
                        <h3>Dismiss Finding</h3>
                        <button class="btn-primary btn-sm" @click="showDismissalForm = !showDismissalForm">
                            <i class="fi fi-rr-plus"></i> Dismiss Finding
                        </button>
                    </div>

                    <div class="section-card-body">
                        <p class="muted" style="margin-bottom: 1rem;">
                            Dismissed findings are stored as admin memory records. The AI memory reconsideration
                            pipeline will suppress similar findings in future reviews. Dismissed patterns can be
                            viewed and managed in the <strong>Memory</strong> tab.
                        </p>

                        <!-- Dismiss form -->
                        <div v-if="showDismissalForm">
                            <div class="form-field">
                                <label>Finding Message <span class="field-hint-inline">(exact AI finding text)</span></label>
                                <textarea
                                    v-model="newDismissal.originalMessage"
                                    rows="3"
                                    placeholder="Paste the exact finding message to suppress"
                                    class="form-input"
                                />
                            </div>
                            <div class="form-field">
                                <label>Label <span class="field-hint-inline">(optional — why it's dismissed)</span></label>
                                <input v-model="newDismissal.label" type="text" placeholder="e.g. False positive: naming style" class="form-input" />
                            </div>
                            <span v-if="dismissalCreateError" class="error">{{ dismissalCreateError }}</span>
                            <div class="form-actions">
                                <button
                                    :disabled="dismissalSaving || !newDismissal.originalMessage.trim()"
                                    class="btn-primary"
                                    @click="handleCreateDismissal"
                                >
                                    {{ dismissalSaving ? 'Saving…' : 'Dismiss Finding' }}
                                </button>
                                <button class="btn-secondary" @click="showDismissalForm = false">Cancel</button>
                            </div>
                        </div>

                        <p v-if="dismissalSuccess" class="success-hint">
                            <i class="fi fi-rr-check-circle"></i> Finding dismissed and stored as a memory record.
                        </p>
                    </div>
                </div>
            </div>

            <!-- Tab: Prompt Overrides -->
            <div v-show="activeTab === 'prompt-overrides'">
                <div class="section-card">
                    <div class="section-card-header">
                        <h3>Prompt Overrides</h3>
                        <button class="btn-primary btn-sm" @click="showOverrideForm = !showOverrideForm">
                            <i class="fi fi-rr-plus"></i> Add Override
                        </button>
                    </div>

                    <!-- Create form -->
                    <div v-if="showOverrideForm" class="section-card-body">
                        <div class="form-field">
                            <label>Prompt Key</label>
                            <select v-model="newOverride.promptKey" class="form-input">
                                <option value="">— select —</option>
                                <option value="SystemPrompt">SystemPrompt</option>
                                <option value="AgenticLoopGuidance">AgenticLoopGuidance</option>
                                <option value="SynthesisSystemPrompt">SynthesisSystemPrompt</option>
                                <option value="QualityFilterSystemPrompt">QualityFilterSystemPrompt</option>
                                <option value="PerFileContextPrompt">PerFileContextPrompt</option>
                            </select>
                        </div>
                        <div class="form-field">
                            <label>Override Text <span class="field-hint-inline">(full replacement for the prompt segment)</span></label>
                            <textarea
                                v-model="newOverride.overrideText"
                                rows="6"
                                placeholder="Enter the full replacement prompt text…"
                                class="form-input"
                            />
                        </div>
                        <span v-if="overrideCreateError" class="error">{{ overrideCreateError }}</span>
                        <div class="form-actions">
                            <button
                                :disabled="overrideSaving || !newOverride.promptKey || !newOverride.overrideText.trim()"
                                class="btn-primary"
                                @click="handleCreateOverride"
                            >
                                {{ overrideSaving ? 'Saving…' : 'Save Override' }}
                            </button>
                            <button class="btn-secondary" @click="showOverrideForm = false">Cancel</button>
                        </div>
                    </div>

                    <div v-if="overridesLoading" class="section-card-body">
                        <p class="muted">Loading prompt overrides…</p>
                    </div>
                    <div v-else-if="overridesError" class="section-card-body">
                        <p class="error">{{ overridesError }}</p>
                    </div>
                    <div v-else-if="promptOverrides.length === 0 && !overridesLoading" class="section-card-body">
                        <p class="muted">No prompt overrides configured for this client.</p>
                    </div>
                    <div v-else class="section-card-body--compact">
                        <table class="admin-table">
                            <thead>
                                <tr>
                                    <th style="width: 250px">Prompt Key</th>
                                    <th>Override Text</th>
                                    <th style="width: 80px" class="text-right">Actions</th>
                                </tr>
                            </thead>
                            <tbody>
                                <tr v-for="o in clientScopedOverrides" :key="o.id">
                                    <td class="font-semibold">{{ o.promptKey }}</td>
                                    <td class="dismissal-pattern-cell">
                                        <div class="pattern-text-wrapper" :title="o.overrideText">
                                            {{ o.overrideText }}
                                        </div>
                                    </td>
                                    <td class="text-right">
                                        <button class="btn-danger btn-xs" title="Delete Override" @click="o.id && handleDeleteOverride(o.id)">
                                            <i class="fi fi-rr-trash"></i>
                                        </button>
                                    </td>
                                </tr>
                            </tbody>
                        </table>
                    </div>
                </div>
            </div>

            <!-- Tab: AI Connections -->
            <div v-show="activeTab === 'ai'">
                <div class="section-card">
                    <div class="section-card-header">
                        <h3>AI Connections</h3>
                        <button class="btn-primary btn-sm" @click="showCreateForm = true">
                            <i class="fi fi-rr-plus"></i> Add Connection
                        </button>
                    </div>

                    <!-- Inline create form -->
                    <div v-if="showCreateForm" class="section-card-body ai-create-form">
                        <div class="ai-form-grid">
                            <div class="form-field">
                                <label>Display Name</label>
                                <input v-model="newConn.displayName" type="text" placeholder="e.g. Azure OpenAI (prod)" />
                            </div>
                            <div class="form-field">
                                <label>Endpoint URL</label>
                                <input v-model="newConn.endpointUrl" type="text" placeholder="https://my-resource.openai.azure.com/" />
                            </div>
                            <div class="form-field">
                                <label>API Key</label>
                                <input v-model="newConn.apiKey" type="password" placeholder="Paste your API key" />
                            </div>
                            <div class="form-field">
                                <label>Models <span class="field-hint-inline">(comma-separated deployment names)</span></label>
                                <input v-model="newConn.modelsInput" type="text" placeholder="e.g. gpt-4o, gpt-4o-mini" />
                            </div>
                            <div class="form-field">
                                <label>Model Category <span class="field-hint-inline">(optional — for tier-based routing)</span></label>
                                <select v-model="newConn.modelCategory">
                                    <option value="">Default (no category)</option>
                                    <option value="lowEffort">Low Effort</option>
                                    <option value="mediumEffort">Medium Effort</option>
                                    <option value="highEffort">High Effort</option>
                                    <option value="embedding">Embedding</option>
                                </select>
                            </div>
                            <div
                                v-if="shouldShowCapabilityEditor(newConn.modelCategory, newConn.modelCapabilities)"
                                class="ai-capability-panel full-col"
                            >
                                <div class="ai-capability-panel-header">
                                    <div>
                                        <h4>Model Capabilities</h4>
                                        <p>
                                            {{ newConn.modelCategory === 'embedding'
                                                ? 'Required for embedding connections. Configure tokenizer, max input tokens, and vector width for each model.'
                                                : 'Optional per-model embedding metadata.' }}
                                        </p>
                                    </div>
                                    <span v-if="newConn.modelCategory === 'embedding'" class="chip chip-info chip-sm">Required</span>
                                </div>
                                <p
                                    v-if="newConn.modelCategory === 'embedding' && !hasCompleteCapabilityInput(newConn.modelCapabilities, parseModelInput(newConn.modelsInput))"
                                    class="muted-hint ai-capability-warning"
                                >
                                    Indexing stays blocked until every configured model has complete capability metadata.
                                </p>
                                <p v-if="newConn.modelCapabilities.length === 0" class="muted-hint ai-capability-empty">
                                    Add at least one model above to configure capabilities.
                                </p>
                                <div v-else class="ai-capability-list">
                                    <div v-for="capability in newConn.modelCapabilities" :key="capability.modelName" class="ai-capability-row">
                                        <div class="ai-capability-name">{{ capability.modelName }}</div>
                                        <div class="form-field">
                                            <label>Tokenizer</label>
                                            <select v-model="capability.tokenizerName" class="capability-select">
                                                <option value="">Select tokenizer</option>
                                                <option v-for="tokenizer in tokenizerOptions" :key="tokenizer" :value="tokenizer">{{ tokenizer }}</option>
                                            </select>
                                        </div>
                                        <div class="form-field">
                                            <label>Max Input Tokens</label>
                                            <input v-model="capability.maxInputTokens" type="number" min="1" placeholder="8192" />
                                        </div>
                                        <div class="form-field">
                                            <label>Dimensions</label>
                                            <input v-model="capability.embeddingDimensions" type="number" min="64" max="4096" placeholder="3072" />
                                        </div>
                                            <div class="form-field">
                                                <label>Input Cost (USD / 1M)</label>
                                                <input v-model="capability.inputCostPer1MUsd" type="number" min="0" step="0.000001" placeholder="0.02" />
                                            </div>
                                            <div class="form-field">
                                                <label>Output Cost (USD / 1M)</label>
                                                <input v-model="capability.outputCostPer1MUsd" type="number" min="0" step="0.000001" placeholder="0" />
                                            </div>
                                    </div>
                                </div>
                            </div>
                        </div>

                        <span v-if="createError" class="error">{{ createError }}</span>
                        <div class="form-actions">
                            <button
                                :disabled="aiLoading || !newConn.modelsInput.trim()"
                                class="btn-primary"
                                @click="handleCreateConnection"
                            >
                                {{ aiLoading ? 'Creating…' : 'Save Connection' }}
                            </button>
                            <button class="btn-secondary" @click="cancelCreateForm">Cancel</button>
                        </div>
                    </div>

                    <div v-if="aiLoading && !showCreateForm" class="section-card-body">
                        <p class="muted">Loading connections…</p>
                    </div>
                    <div v-else-if="aiError" class="section-card-body">
                        <p class="error">{{ aiError }}</p>
                    </div>
                    <div v-else-if="aiConnections.length === 0 && !showCreateForm" class="section-card-body">
                        <p class="muted">No AI connections configured for this client.</p>
                    </div>
                    <ul v-else class="ai-connections-list">
                        <li v-for="conn in aiConnections" :key="conn.id" class="ai-connection-item">
                            <div
                                class="ai-conn-main ai-conn-main--clickable"
                                role="button"
                                tabindex="0"
                                @click="openEditModal(conn)"
                                @keydown.enter.prevent="openEditModal(conn)"
                                @keydown.space.prevent="openEditModal(conn)"
                            >
                                <div class="ai-conn-header">
                                    <span class="ai-conn-name">{{ conn.displayName }}</span>
                                    <template v-if="!conn.modelCategory">
                                      <span v-if="conn.isActive" class="chip chip-success chip-sm">
                                          <i class="fi fi-rr-check-circle"></i> Active
                                          <span v-if="conn.activeModel" class="active-model-label">— {{ conn.activeModel }}</span>
                                      </span>
                                      <span v-else class="chip chip-muted chip-sm">Inactive</span>
                                    </template>
                                    <template v-else>
                                      <span v-if="conn.activeModel" class="chip chip-success chip-sm">
                                          <i class="fi fi-rr-check-circle"></i> {{ conn.activeModel }}
                                      </span>
                                      <span v-else class="chip chip-muted chip-sm">No model selected</span>
                                    </template>
                                    <span
                                        v-if="conn.modelCategory"
                                        :class="['chip', getModelCategoryChipClass(conn.modelCategory), 'chip-sm', 'tier-badge']"
                                    >
                                        {{ formatModelCategory(conn.modelCategory) }}
                                    </span>
                                    <span
                                        v-if="conn.modelCategory === 'embedding'"
                                        :class="['chip', hasCompleteEmbeddingCapabilities(conn) ? 'chip-info' : 'chip-muted', 'chip-sm']"
                                    >
                                        {{ hasCompleteEmbeddingCapabilities(conn) ? 'Capabilities Ready' : 'Capabilities Missing' }}
                                    </span>
                                </div>
                                <div class="ai-conn-meta">
                                    <span class="ai-conn-endpoint">{{ conn.endpointUrl }}</span>
                                    <span class="ai-conn-edit-hint">Click to edit</span>
                                </div>
                                <div v-if="conn.models && conn.models.length" class="ai-conn-models">
                                    <span v-for="m in conn.models" :key="m" class="model-chip">{{ m }}</span>
                                </div>
                            </div>
                            <div class="ai-conn-actions">
                                <!-- Categorized connections: just pick a model, no activate/deactivate -->
                                <template v-if="conn.modelCategory">
                                    <button class="btn-secondary btn-sm" @click="openActivateModal(conn)">
                                        {{ conn.activeModel ? 'Change Model' : 'Select Model' }}
                                    </button>
                                </template>
                                <!-- Default connections: activate/deactivate flow -->
                                <template v-else>
                                    <button v-if="!conn.isActive" class="btn-secondary btn-sm" @click="openActivateModal(conn)">
                                        Activate
                                    </button>
                                    <template v-else>
                                        <button class="btn-secondary btn-sm" @click="openActivateModal(conn)">
                                            Change Model
                                        </button>
                                        <button class="btn-secondary btn-sm" @click="conn.id && handleDeactivate(conn.id)">
                                            Deactivate
                                        </button>
                                    </template>
                                </template>
                                <button class="btn-danger btn-sm" @click="conn.id && handleDeleteConnection(conn.id)">
                                    <i class="fi fi-rr-trash"></i>
                                </button>
                            </div>
                        </li>
                    </ul>
                </div>

                <ModalDialog :isOpen="editModal.open" title="Edit AI connection" @update:isOpen="editModal.open = $event">
                    <div class="ai-form-grid ai-edit-form-grid">
                        <div class="form-field">
                            <label for="editAiDisplayName">Display Name</label>
                            <input id="editAiDisplayName" v-model="editModal.displayName" name="editAiDisplayName" type="text" />
                        </div>
                        <div class="form-field">
                            <label for="editAiEndpointUrl">Endpoint URL</label>
                            <input id="editAiEndpointUrl" v-model="editModal.endpointUrl" name="editAiEndpointUrl" type="text" />
                        </div>
                        <div class="form-field">
                            <label for="editAiModels">Models <span class="field-hint-inline">(comma-separated deployment names)</span></label>
                            <input id="editAiModels" v-model="editModal.modelsInput" name="editAiModels" type="text" />
                        </div>
                        <div class="form-field">
                            <label for="editAiApiKey">API Key <span class="field-hint-inline">(leave blank to keep existing)</span></label>
                            <input id="editAiApiKey" v-model="editModal.apiKey" name="editAiApiKey" type="password" />
                        </div>
                        <div v-if="editModal.modelCategoryLabel" class="form-field ai-edit-form-grid-full">
                            <label>Model Category</label>
                            <div class="readonly-value">{{ editModal.modelCategoryLabel }}</div>
                        </div>
                        <div
                            v-if="shouldShowCapabilityEditor(editModal.modelCategory, editModal.modelCapabilities)"
                            class="ai-capability-panel ai-edit-form-grid-full"
                        >
                            <div class="ai-capability-panel-header">
                                <div>
                                    <h4>Model Capabilities</h4>
                                    <p>
                                        {{ editModal.modelCategory === 'embedding'
                                            ? 'Required for embedding connections. Configure tokenizer, max input tokens, and vector width for each model.'
                                            : 'Optional per-model embedding metadata.' }}
                                    </p>
                                </div>
                                <span v-if="editModal.modelCategory === 'embedding'" class="chip chip-info chip-sm">Required</span>
                            </div>
                            <p
                                v-if="editModal.modelCategory === 'embedding' && !hasCompleteCapabilityInput(editModal.modelCapabilities, parseModelInput(editModal.modelsInput))"
                                class="muted-hint ai-capability-warning"
                            >
                                ProCursor indexing remains blocked until every configured model has complete capability metadata.
                            </p>
                            <p v-if="editModal.modelCapabilities.length === 0" class="muted-hint ai-capability-empty">
                                Add at least one model above to configure capabilities.
                            </p>
                            <div v-else class="ai-capability-list">
                                <div v-for="capability in editModal.modelCapabilities" :key="capability.modelName" class="ai-capability-row">
                                    <div class="ai-capability-name">{{ capability.modelName }}</div>
                                    <div class="form-field">
                                        <label>Tokenizer</label>
                                        <select v-model="capability.tokenizerName" class="capability-select">
                                            <option value="">Select tokenizer</option>
                                            <option v-for="tokenizer in tokenizerOptions" :key="tokenizer" :value="tokenizer">{{ tokenizer }}</option>
                                        </select>
                                    </div>
                                    <div class="form-field">
                                        <label>Max Input Tokens</label>
                                        <input v-model="capability.maxInputTokens" type="number" min="1" placeholder="8192" />
                                    </div>
                                    <div class="form-field">
                                        <label>Dimensions</label>
                                        <input v-model="capability.embeddingDimensions" type="number" min="64" max="4096" placeholder="3072" />
                                    </div>
                                    <div class="form-field">
                                        <label>Input Cost (USD / 1M)</label>
                                        <input v-model="capability.inputCostPer1MUsd" type="number" min="0" step="0.000001" placeholder="0.02" />
                                    </div>
                                    <div class="form-field">
                                        <label>Output Cost (USD / 1M)</label>
                                        <input v-model="capability.outputCostPer1MUsd" type="number" min="0" step="0.000001" placeholder="0" />
                                    </div>
                                </div>
                            </div>
                        </div>
                    </div>

                    <p v-if="editError" class="error">{{ editError }}</p>

                    <template #footer>
                        <button class="btn-secondary" @click="editModal.open = false">Cancel</button>
                        <button class="btn-primary save-ai-edit-btn" :disabled="editSaving" @click="handleSaveConnectionEdit">
                            {{ editSaving ? 'Saving…' : 'Save Changes' }}
                        </button>
                    </template>
                </ModalDialog>

                <!-- Activate modal -->
                <div v-if="activateModal.open" class="modal-backdrop" @click.self="activateModal.open = false">
                    <div class="modal-card">
                        <h3>{{ activateModal.conn?.modelCategory ? 'Select Model' : 'Activate Connection' }}</h3>
                        <p>
                            Select the model to use for <strong>{{ activateModal.conn?.displayName }}</strong>
                            <template v-if="activateModal.conn?.modelCategory">
                                <span class="chip chip-muted chip-sm tier-badge">{{ formatModelCategory(activateModal.conn.modelCategory) }}</span>
                            </template>:
                        </p>
                        <select v-model="activateModal.selectedModel" class="modal-select">
                            <option v-for="m in activateModal.conn?.models ?? []" :key="m" :value="m">{{ m }}</option>
                        </select>
                        <div class="form-actions modal-actions">
                            <button :disabled="aiLoading" class="btn-primary" @click="handleActivate">
                                {{ aiLoading ? 'Saving…' : (activateModal.conn?.modelCategory ? 'Save' : 'Activate') }}
                            </button>
                            <button class="btn-secondary" @click="activateModal.open = false">Cancel</button>
                        </div>
                        <p v-if="activateError" class="error">{{ activateError }}</p>
                    </div>
                </div>
            </div>
        </template>
        </main>

        <TextViewerModal 
            :isOpen="isTextViewerOpen" 
            @update:isOpen="isTextViewerOpen = $event"
            :title="textViewerTitle" 
            :text="textViewerContent"
            plain-text
        />
    </div>
</template>

<script lang="ts" setup>
import {onMounted, ref, reactive, computed, watch} from 'vue'
import {RouterLink, useRoute, useRouter} from 'vue-router'
import ClientCrawlConfigsTab from '@/components/ClientCrawlConfigsTab.vue'
import ClientWebhookConfigsTab from '@/components/ClientWebhookConfigsTab.vue'
import ClientProviderConnectionsTab from '@/components/ClientProviderConnectionsTab.vue'
import ProviderConnectionStatusList from '@/components/ProviderConnectionStatusList.vue'
import ProviderConnectionAuditTrail from '@/components/ProviderConnectionAuditTrail.vue'
import ClientProCursorTab from '@/components/ClientProCursorTab.vue'
import UsageDashboard from '@/components/UsageDashboard.vue'
import ConfirmDialog from '@/components/ConfirmDialog.vue'
import ReviewHistorySection from '@/components/ReviewHistorySection.vue'
import TextViewerModal from '@/components/TextViewerModal.vue'
import ModalDialog from '@/components/ModalDialog.vue'
import {createAdminClient} from '@/services/api'
import {
    listAiConnections,
    createAiConnection,
    updateAiConnection,
    activateAiConnection,
    deactivateAiConnection,
    deleteAiConnection,
} from '@/services/aiConnectionsService'
import type {
    AiConnectionDto,
    AiConnectionModelCapabilityDto,
    AiConnectionModelCapabilityRequest,
    CreateAiConnectionRequest,
    UpdateAiConnectionRequest,
} from '@/services/aiConnectionsService'
import {
    dismissFinding,
} from '@/services/findingDismissalsService'
import { listOverrides, createOverride, deleteOverride } from '@/services/promptOverridesService'
import type { components } from '@/types'
type PromptOverrideDto = components['schemas']['PromptOverrideDto']

interface Client {
    id: string
    displayName: string
    isActive: boolean
    reviewerId?: string | null
    createdAt: string
}

const router = useRouter()
const route = useRoute()
const clientId = route.params.id as string

const client = ref<Client | null>(null)
const loading = ref(false)
const notFound = ref(false)
const saving = ref(false)
const saveError = ref('')
const showDeleteDialog = ref(false)
const editedDisplayName = ref('')
const activeTab = ref<'config' | 'crawl-configs' | 'webhooks' | 'providers' | 'procursor' | 'ai' | 'history' | 'dismissals' | 'prompt-overrides' | 'usage'>('config')
const isProCursorTokenUsageReportingEnabled = import.meta.env.VITE_FEATURE_PROCURSOR_TOKEN_USAGE_REPORTING !== 'false'

// Text Viewer Modal
const isTextViewerOpen = ref(false)
const textViewerTitle = ref('')
const textViewerContent = ref('')

// AI Connections tab
const aiConnections = ref<AiConnectionDto[]>([])
const aiLoading = ref(false)
const aiError = ref('')
const showCreateForm = ref(false)
const createError = ref('')
const activateError = ref('')
const editError = ref('')
const editSaving = ref(false)

type EditableModelCategory = NonNullable<AiConnectionDto['modelCategory']> | ''

type EditableModelCapability = {
    modelName: string
    tokenizerName: string
    maxInputTokens: string | number
    embeddingDimensions: string | number
    inputCostPer1MUsd: string | number
    outputCostPer1MUsd: string | number
}

const tokenizerOptions = [
    'cl100k_base',
    'o200k_base',
    'o200k_harmony',
    'r50k_base',
    'p50k_base',
    'p50k_edit',
    'claude',
] as const

const newConn = reactive({
    displayName: '',
    endpointUrl: '',
    apiKey: '',
    modelsInput: '',
    modelCategory: '' as EditableModelCategory,
    modelCapabilities: [] as EditableModelCapability[],
})

const editModal = reactive({
    open: false,
    connectionId: '',
    displayName: '',
    endpointUrl: '',
    apiKey: '',
    modelsInput: '',
    modelCategoryLabel: '',
    modelCategory: '' as EditableModelCategory,
    modelCapabilities: [] as EditableModelCapability[],
})

const activateModal = reactive<{
    open: boolean
    conn: AiConnectionDto | null
    selectedModel: string
}>({
    open: false,
    conn: null,
    selectedModel: '',
})

// Dismissed Findings tab — dismissals are now stored as memory records
const dismissalsLoading = ref(false)
const dismissalsError = ref('')
const newDismissal = reactive({ originalMessage: '', label: '' })
const dismissalCreateError = ref('')
const dismissalSaving = ref(false)
const showDismissalForm = ref(false)
const dismissalSuccess = ref(false)

// Prompt Overrides tab
const promptOverrides = ref<PromptOverrideDto[]>([])
const overridesLoading = ref(false)
const overridesError = ref('')
const showOverrideForm = ref(false)
const overrideSaving = ref(false)
const overrideCreateError = ref('')
const newOverride = reactive({
    scope: 'clientScope' as 'clientScope' | 'crawlConfigScope',
    promptKey: '',
    overrideText: '',
})

const clientScopedOverrides = computed(() =>
    promptOverrides.value.filter(o => o.scope === 'clientScope')
)

watch(() => newConn.modelsInput, () => {
    if (newConn.modelCategory === 'embedding' || newConn.modelCapabilities.length > 0) {
        syncNewConnectionCapabilities()
    }
})

watch(() => newConn.modelCategory, (modelCategory) => {
    if (modelCategory === 'embedding') {
        syncNewConnectionCapabilities()
    }
})

watch(() => editModal.modelsInput, () => {
    if (editModal.modelCategory === 'embedding' || editModal.modelCapabilities.length > 0) {
        syncEditConnectionCapabilities()
    }
})

onMounted(async () => {
    loading.value = true
    try {
        const {data, response} = await createAdminClient().GET('/clients/{clientId}', {
            params: {path: {clientId}},
        })
        if (response && (response as Response).status === 404) {
            notFound.value = true
            router.push({ name: 'clients' })
            return
        }
        client.value = data as Client
        editedDisplayName.value = (data as Client).displayName
    } catch {
        notFound.value = true
        router.push({ name: 'clients' })
    } finally {
        loading.value = false
    }
})

async function saveDisplayName() {
    if (!client.value) return
    saving.value = true
    saveError.value = ''
    try {
        const {data} = await createAdminClient().PATCH('/clients/{clientId}', {
            params: {path: {clientId}},
            body: {displayName: editedDisplayName.value},
        })
        client.value = data as Client
    } catch {
        saveError.value = 'Failed to save.'
    } finally {
        saving.value = false
    }
}

async function toggleStatus() {
    if (!client.value) return
    saving.value = true
    try {
        const {data} = await createAdminClient().PATCH('/clients/{clientId}', {
            params: {path: {clientId}},
            body: {isActive: !client.value.isActive},
        })
        client.value = data as Client
    } catch {
        saveError.value = 'Failed to update status.'
    } finally {
        saving.value = false
    }
}

async function handleDelete() {
    try {
        await createAdminClient().DELETE('/clients/{clientId}', {
            params: {path: {clientId}},
        })
        router.push({ name: 'clients' })
    } catch {
        router.push({ name: 'clients' })
    }
}

async function loadAiConnections() {
    if (aiLoading.value) return
    aiLoading.value = true
    aiError.value = ''
    try {
        aiConnections.value = await listAiConnections(clientId)
    } catch {
        aiError.value = 'Failed to load AI connections.'
    } finally {
        aiLoading.value = false
    }
}

async function refreshAiConnections() {
    aiError.value = ''
    try {
        aiConnections.value = await listAiConnections(clientId)
    } catch {
        aiError.value = 'Failed to refresh AI connections.'
    }
}

async function handleCreateConnection() {
    createError.value = ''
    aiLoading.value = true
    try {
        const models = parseModelInput(newConn.modelsInput)
        const capabilityResult = buildCapabilityRequest(
            models,
            newConn.modelCapabilities,
            newConn.modelCategory === 'embedding',
        )
        newConn.modelCapabilities = capabilityResult.syncedCapabilities

        if (capabilityResult.error) {
            createError.value = capabilityResult.error
            return
        }

        const request: CreateAiConnectionRequest = {
            displayName: newConn.displayName,
            endpointUrl: newConn.endpointUrl,
            models,
            apiKey: newConn.apiKey,
            modelCategory: newConn.modelCategory || undefined,
            modelCapabilities: capabilityResult.modelCapabilities,
        }

        const conn = await createAiConnection(clientId, request)
        aiConnections.value.push(conn)
        cancelCreateForm()
    } catch (error) {
        createError.value = error instanceof Error ? error.message : 'Failed to create connection.'
    } finally {
        aiLoading.value = false
    }
}

function cancelCreateForm() {
    showCreateForm.value = false
    createError.value = ''
    newConn.displayName = ''
    newConn.endpointUrl = ''
    newConn.apiKey = ''
    newConn.modelsInput = ''
    newConn.modelCategory = ''
    newConn.modelCapabilities = []
}

function formatModelCategory(modelCategory: AiConnectionDto['modelCategory']): string {
    switch (modelCategory) {
        case 'lowEffort':
            return 'Low Effort'
        case 'mediumEffort':
            return 'Medium Effort'
        case 'highEffort':
            return 'High Effort'
        case 'embedding':
            return 'Embedding'
        case 'memoryReconsideration':
            return 'Memory Reconsideration'
        case 'default':
            return 'Default'
        default:
            return ''
    }
}

function getModelCategoryChipClass(modelCategory: AiConnectionDto['modelCategory']): string {
    switch (modelCategory) {
        case 'highEffort':
            return 'chip-accent'
        case 'embedding':
            return 'chip-info'
        default:
            return 'chip-muted'
    }
}

function parseModelInput(modelsInput: string): string[] {
    const seen = new Set<string>()
    const models: string[] = []

    for (const rawModel of modelsInput.split(',')) {
        const model = rawModel.trim()
        const normalizedModel = model.toLowerCase()

        if (!model || seen.has(normalizedModel)) {
            continue
        }

        seen.add(normalizedModel)
        models.push(model)
    }

    return models
}

function toEditableModelCapabilities(
    capabilities: AiConnectionModelCapabilityDto[] | null | undefined,
): EditableModelCapability[] {
    return (capabilities ?? []).map(capability => ({
        modelName: capability.modelName ?? '',
        tokenizerName: capability.tokenizerName ?? '',
        maxInputTokens: capability.maxInputTokens == null ? '' : String(capability.maxInputTokens),
        embeddingDimensions: capability.embeddingDimensions == null ? '' : String(capability.embeddingDimensions),
        inputCostPer1MUsd: capability.inputCostPer1MUsd == null ? '' : String(capability.inputCostPer1MUsd),
        outputCostPer1MUsd: capability.outputCostPer1MUsd == null ? '' : String(capability.outputCostPer1MUsd),
    }))
}

function syncCapabilityInputs(
    models: string[],
    currentCapabilities: EditableModelCapability[],
): EditableModelCapability[] {
    const existingCapabilities = new Map(
        currentCapabilities
            .filter(capability => capability.modelName)
            .map(capability => [capability.modelName.toLowerCase(), capability]),
    )

    return models.map(model => {
        const existing = existingCapabilities.get(model.toLowerCase())
        return {
            modelName: model,
            tokenizerName: existing?.tokenizerName ?? '',
            maxInputTokens: existing?.maxInputTokens ?? '',
            embeddingDimensions: existing?.embeddingDimensions ?? '',
            inputCostPer1MUsd: existing?.inputCostPer1MUsd ?? '',
            outputCostPer1MUsd: existing?.outputCostPer1MUsd ?? '',
        }
    })
}

function syncNewConnectionCapabilities() {
    newConn.modelCapabilities = syncCapabilityInputs(parseModelInput(newConn.modelsInput), newConn.modelCapabilities)
}

function syncEditConnectionCapabilities() {
    editModal.modelCapabilities = syncCapabilityInputs(parseModelInput(editModal.modelsInput), editModal.modelCapabilities)
}

function shouldShowCapabilityEditor(
    modelCategory: EditableModelCategory | AiConnectionDto['modelCategory'],
    modelCapabilities: EditableModelCapability[],
): boolean {
    return modelCategory === 'embedding' || modelCapabilities.length > 0
}

function parsePositiveInteger(value: string | number): number | null {
    const normalizedValue = String(value ?? '').trim()
    if (!normalizedValue) {
        return null
    }

    const parsed = Number.parseInt(normalizedValue, 10)
    if (!Number.isInteger(parsed) || parsed <= 0) {
        return null
    }

    return parsed
}

function hasAnyCapabilityValue(capability: EditableModelCapability): boolean {
    return Boolean(
        capability.tokenizerName.trim() ||
        String(capability.maxInputTokens ?? '').trim() ||
        String(capability.embeddingDimensions ?? '').trim() ||
        String(capability.inputCostPer1MUsd ?? '').trim() ||
        String(capability.outputCostPer1MUsd ?? '').trim(),
    )
}

function isValidEmbeddingDimensions(value: string | number): boolean {
    const parsed = parsePositiveInteger(value)
    return parsed !== null && parsed >= 64 && parsed <= 4096
}

function parseNonNegativeNumber(value: string | number): number | null {
    const normalized = String(value ?? '').trim()
    if (!normalized) return null
    const parsed = Number(normalized)
    if (!Number.isFinite(parsed) || parsed < 0) return null
    return parsed
}

function hasCompleteCapabilityInput(
    modelCapabilities: EditableModelCapability[],
    models: string[],
): boolean {
    if (models.length === 0) {
        return false
    }

    const syncedCapabilities = syncCapabilityInputs(models, modelCapabilities)
    return syncedCapabilities.every(capability =>
        capability.tokenizerName.trim() &&
        parsePositiveInteger(capability.maxInputTokens) !== null &&
        isValidEmbeddingDimensions(capability.embeddingDimensions),
    )
}

function hasCompleteEmbeddingCapabilities(conn: AiConnectionDto): boolean {
    const models = conn.models ?? []
    const capabilityEntries: Array<[string, AiConnectionModelCapabilityDto]> = (conn.modelCapabilities ?? [])
        .filter((capability): capability is AiConnectionModelCapabilityDto => Boolean(capability.modelName))
        .map(capability => [capability.modelName!.toLowerCase(), capability])
    const capabilityMap = new Map<string, AiConnectionModelCapabilityDto>(capabilityEntries)

    return models.length > 0 && models.every(model => {
        const capability = capabilityMap.get(model.toLowerCase())
        return Boolean(
            capability?.tokenizerName &&
            capability.maxInputTokens && capability.maxInputTokens > 0 &&
            capability.embeddingDimensions && capability.embeddingDimensions >= 64 && capability.embeddingDimensions <= 4096,
        )
    })
}

function buildCapabilityRequest(
    models: string[],
    modelCapabilities: EditableModelCapability[],
    requireComplete: boolean,
): {
    error?: string
    modelCapabilities?: AiConnectionModelCapabilityRequest[]
    syncedCapabilities: EditableModelCapability[]
} {
    const syncedCapabilities = syncCapabilityInputs(models, modelCapabilities)
    const requestCapabilities: AiConnectionModelCapabilityRequest[] = []

    for (const capability of syncedCapabilities) {
        const hasValues = hasAnyCapabilityValue(capability)
        if (!hasValues) {
            continue
        }

        if (!capability.tokenizerName.trim()) {
            return {
                error: `Tokenizer is required for model '${capability.modelName}'.`,
                syncedCapabilities,
            }
        }

        if (!tokenizerOptions.includes(capability.tokenizerName as (typeof tokenizerOptions)[number])) {
            return {
                error: `Tokenizer '${capability.tokenizerName}' is not supported.`,
                syncedCapabilities,
            }
        }

        const maxInputTokens = parsePositiveInteger(capability.maxInputTokens)
        if (maxInputTokens === null) {
            return {
                error: `Max input tokens must be a positive integer for model '${capability.modelName}'.`,
                syncedCapabilities,
            }
        }

        const embeddingDimensions = parsePositiveInteger(capability.embeddingDimensions)
        if (embeddingDimensions === null || embeddingDimensions < 64 || embeddingDimensions > 4096) {
            return {
                error: `Embedding dimensions must be between 64 and 4096 for model '${capability.modelName}'.`,
                syncedCapabilities,
            }
        }

        const inputCost = parseNonNegativeNumber(capability.inputCostPer1MUsd)
        if (String(capability.inputCostPer1MUsd ?? '').trim() && inputCost === null) {
            return { error: `Input cost must be a non-negative number for model '${capability.modelName}'.`, syncedCapabilities }
        }

        const outputCost = parseNonNegativeNumber(capability.outputCostPer1MUsd)
        if (String(capability.outputCostPer1MUsd ?? '').trim() && outputCost === null) {
            return { error: `Output cost must be a non-negative number for model '${capability.modelName}'.`, syncedCapabilities }
        }

        const capabilityRequest: AiConnectionModelCapabilityRequest = {
            modelName: capability.modelName,
            tokenizerName: capability.tokenizerName,
            maxInputTokens,
            embeddingDimensions,
        }

        if (inputCost !== null) capabilityRequest.inputCostPer1MUsd = inputCost
        if (outputCost !== null) capabilityRequest.outputCostPer1MUsd = outputCost

        requestCapabilities.push(capabilityRequest)
    }

    if (requireComplete && requestCapabilities.length !== models.length) {
        return {
            error: 'Embedding connections require capability metadata for every configured model.',
            syncedCapabilities,
        }
    }

    return {
        modelCapabilities: requestCapabilities.length > 0 || requireComplete ? requestCapabilities : undefined,
        syncedCapabilities,
    }
}

function openEditModal(conn: AiConnectionDto) {
    editError.value = ''
    editModal.connectionId = conn.id ?? ''
    editModal.displayName = conn.displayName ?? ''
    editModal.endpointUrl = conn.endpointUrl ?? ''
    editModal.apiKey = ''
    editModal.modelsInput = (conn.models ?? []).join(', ')
    editModal.modelCategoryLabel = formatModelCategory(conn.modelCategory)
    editModal.modelCategory = conn.modelCategory ?? ''
    editModal.modelCapabilities = syncCapabilityInputs(
        conn.models ?? [],
        toEditableModelCapabilities(conn.modelCapabilities),
    )
    editModal.open = true
}

async function handleSaveConnectionEdit() {
    if (!editModal.connectionId) {
        return
    }

    const models = parseModelInput(editModal.modelsInput)
    if (models.length === 0) {
        editError.value = 'At least one model is required.'
        return
    }

    editSaving.value = true
    editError.value = ''
    try {
        const capabilityResult = buildCapabilityRequest(
            models,
            editModal.modelCapabilities,
            editModal.modelCategory === 'embedding',
        )
        editModal.modelCapabilities = capabilityResult.syncedCapabilities

        if (capabilityResult.error) {
            editError.value = capabilityResult.error
            return
        }

        const request: UpdateAiConnectionRequest = {
            displayName: editModal.displayName,
            endpointUrl: editModal.endpointUrl,
            models,
            modelCapabilities: capabilityResult.modelCapabilities,
        }

        if (editModal.apiKey.trim()) {
            request.apiKey = editModal.apiKey.trim()
        }

        await updateAiConnection(clientId, editModal.connectionId, request)
        await refreshAiConnections()
        editModal.open = false
    } catch (error) {
        editError.value = error instanceof Error ? error.message : 'Failed to update connection.'
    } finally {
        editSaving.value = false
    }
}

function openActivateModal(conn: AiConnectionDto) {
    activateModal.conn = conn
    activateModal.selectedModel = (conn.models ?? [])[0] ?? ''
    activateModal.open = true
    activateError.value = ''
}

async function handleActivate() {
    if (!activateModal.conn?.id || !activateModal.selectedModel) return
    aiLoading.value = true
    activateError.value = ''
    try {
        await activateAiConnection(clientId, activateModal.conn.id, activateModal.selectedModel)
        await refreshAiConnections()
        activateModal.open = false
    } catch {
        activateError.value = 'Failed to activate connection.'
    } finally {
        aiLoading.value = false
    }
}

async function handleDeactivate(connectionId: string) {
    aiLoading.value = true
    try {
        await deactivateAiConnection(clientId, connectionId)
        await refreshAiConnections()
    } catch {
        aiError.value = 'Failed to deactivate connection.'
    } finally {
        aiLoading.value = false
    }
}

async function handleDeleteConnection(connectionId: string) {
    aiLoading.value = true
    try {
        await deleteAiConnection(clientId, connectionId)
        aiConnections.value = aiConnections.value.filter((c: any) => c.id !== connectionId)
    } catch {
        aiError.value = 'Failed to delete connection.'
    } finally {
        aiLoading.value = false
    }
}

// ─── Dismissed Findings handlers ─────────────────────────────────────────────

async function loadDismissals() {
    // No-op: dismissals are now stored as memory records and viewed through the Memory tab.
}

async function handleCreateDismissal() {
    dismissalCreateError.value = ''
    dismissalSaving.value = true
    dismissalSuccess.value = false
    try {
        await dismissFinding(clientId, {
            findingMessage: newDismissal.originalMessage,
            label: newDismissal.label || null,
        })
        newDismissal.originalMessage = ''
        newDismissal.label = ''
        showDismissalForm.value = false
        dismissalSuccess.value = true
        setTimeout(() => { dismissalSuccess.value = false }, 3000)
    } catch {
        dismissalCreateError.value = 'Failed to dismiss finding.'
    } finally {
        dismissalSaving.value = false
    }
}

async function loadPromptOverrides() {
    if (overridesLoading.value) return
    overridesLoading.value = true
    overridesError.value = ''
    try {
        promptOverrides.value = await listOverrides(clientId)
    } catch {
        overridesError.value = 'Failed to load prompt overrides.'
    } finally {
        overridesLoading.value = false
    }
}

async function handleCreateOverride() {
    overrideCreateError.value = ''
    overrideSaving.value = true
    try {
        const o = await createOverride(clientId, {
            scope: 'clientScope',
            promptKey: newOverride.promptKey,
            overrideText: newOverride.overrideText,
        })
        promptOverrides.value.push(o)
        newOverride.promptKey = ''
        newOverride.overrideText = ''
        showOverrideForm.value = false
    } catch {
        overrideCreateError.value = 'Failed to save override. A duplicate scope+key may already exist.'
    } finally {
        overrideSaving.value = false
    }
}

async function handleDeleteOverride(id: string) {
    try {
        await deleteOverride(clientId, id)
        promptOverrides.value = promptOverrides.value.filter(o => o.id !== id)
    } catch {
        overridesError.value = 'Failed to delete override.'
    }
}
</script>

<style scoped>
.success {
    color: var(--color-success);
    margin-left: 0.5rem;
    font-weight: 500;
}

.toggle-status-btn {
    font-size: 0.8rem;
    padding: 0.35rem 0.85rem;
}

/* Compact section body variant */
.section-card-body--compact {
    padding: 1rem 1.25rem;
}

/* Inline field row — input + button on same line */
.inline-field-row {
    display: flex;
    align-items: flex-end;
    gap: 0.75rem;
}
.flex-1 { flex: 1; }
.inline-save-btn {
    flex-shrink: 0;
    align-self: flex-end;
    margin-bottom: 0;
}

/* 2-column grid for reviewer fields */
.reviewer-fields-grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.75rem;
    margin-bottom: 0.75rem;
}

.identity-save-actions {
    margin-top: 1rem;
    border-top: 1px solid var(--color-border);
    padding-top: 1rem;
}

/* Locked section state */
.section-card--locked .section-card-header {
    opacity: 0.65;
}

.section-locked-notice {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 1rem 1.25rem;
    color: var(--color-text-muted);
    font-size: 0.875rem;
    border-top: 1px solid var(--color-border);
}

.section-locked-notice .fi {
    font-size: 0.9rem;
    flex-shrink: 0;
}

.section-locked-notice p {
    margin: 0;
}

.identity-list {
    list-style: none;
    padding: 0;
    margin: 0.75rem 0 0;
}

.identity-list li {
    padding: 0.6rem 0.875rem;
    border: 1px solid var(--color-border);
    border-radius: 8px;
    margin-bottom: 0.4rem;
    cursor: pointer;
    display: flex;
    flex-direction: column;
    background: var(--color-bg);
    transition: all 0.2s;
}

.identity-list li:hover {
    border-color: var(--color-text-muted);
}

.identity-list li.selected {
    border-color: var(--color-accent);
    background: rgba(34, 211, 238, 0.04);
}

.guid {
    font-size: 0.78rem;
    color: var(--color-text-muted);
    font-family: monospace;
    margin-top: 0.2rem;
}

/* AI Connections tab */
.btn-sm {
    font-size: 0.8rem;
    padding: 0.3rem 0.7rem;
}

.ai-create-form {
    border-top: 1px solid var(--color-border);
    padding: 1rem 1.25rem;
    display: flex;
    flex-direction: column;
    gap: 1rem;
}

.ai-form-grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.75rem;
}

.ai-form-grid .full-col {
    grid-column: span 2;
}

.ai-capability-panel {
    grid-column: span 2;
    border: 1px solid var(--color-border);
    border-radius: 10px;
    background: rgba(255, 255, 255, 0.03);
    padding: 1rem;
    display: flex;
    flex-direction: column;
    gap: 0.85rem;
}

.ai-capability-panel-header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 1rem;
}

.ai-capability-panel-header h4 {
    margin: 0;
    font-size: 0.92rem;
}

.ai-capability-panel-header p {
    margin: 0.25rem 0 0;
    color: var(--color-text-muted);
    font-size: 0.82rem;
}

.ai-capability-empty,
.ai-capability-warning {
    margin: 0;
    padding: 0;
}

.ai-capability-list {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
}

.ai-capability-row {
    display: grid;
    grid-template-columns: minmax(0, 1.2fr) 1fr 1fr 1fr 1fr 1fr;
    gap: 0.75rem;
    align-items: end;
    padding: 0.9rem;
    border: 1px solid var(--color-border);
    border-radius: 10px;
    background: rgba(255, 255, 255, 0.02);
}

.ai-capability-row .form-field {
    margin-bottom: 0;
}

.ai-capability-name {
    font-family: monospace;
    font-size: 0.84rem;
    word-break: break-word;
    padding-bottom: 0.8rem;
}

.capability-select {
    width: 100%;
    min-height: 42px;
}

.field-hint-inline {
    font-weight: normal;
    font-size: 0.78rem;
    color: var(--color-text-muted);
    text-transform: none;
    letter-spacing: 0;
}

.ai-connections-list {
    list-style: none;
    margin: 0;
    padding: 0;
    border-top: 1px solid var(--color-border);
}

.ai-connection-item {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 0.85rem 1.25rem;
    border-bottom: 1px solid var(--color-border);
    gap: 1rem;
}

.ai-connection-item:last-child {
    border-bottom: none;
}

.ai-conn-main {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
}

.ai-conn-main--clickable {
    cursor: pointer;
    border-radius: 10px;
    padding: 0.25rem 0.35rem;
    margin: -0.25rem -0.35rem;
    transition: background 0.2s ease, border-color 0.2s ease;
}

.ai-conn-main--clickable:hover,
.ai-conn-main--clickable:focus-visible {
    background: rgba(34, 211, 238, 0.06);
    outline: none;
}

.ai-conn-header {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    flex-wrap: wrap;
}

.ai-conn-name {
    font-weight: 600;
    font-size: 0.95rem;
}

.chip-sm {
    font-size: 0.72rem;
    padding: 0.15rem 0.5rem;
}

.active-model-label {
    font-weight: normal;
    opacity: 0.8;
}

.ai-conn-meta {
    display: flex;
    flex-wrap: wrap;
    gap: 0.75rem;
    font-size: 0.78rem;
    color: var(--color-text-muted);
    font-family: monospace;
}

.ai-conn-edit-hint {
    font-family: inherit;
    letter-spacing: 0.02em;
}

.ai-conn-models {
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
    margin-top: 0.25rem;
}

.model-chip {
    font-size: 0.75rem;
    background: rgba(255, 255, 255, 0.07);
    border: 1px solid var(--color-border);
    border-radius: 4px;
    padding: 0.1rem 0.45rem;
    font-family: monospace;
}

.ai-conn-actions {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-shrink: 0;
}

.muted {
    color: var(--color-text-muted);
    font-style: italic;
    padding: 1rem 1.25rem;
}

/* Modal */
.modal-backdrop {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.55);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1000;
}

.modal-card {
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: 12px;
    padding: 1.5rem;
    width: 360px;
    max-width: 90vw;
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);
}

.modal-card h3 {
    margin: 0;
    font-size: 1.05rem;
}

.modal-select {
    width: 100%;
}

.modal-actions {
    margin-top: 0.25rem;
}

.ai-edit-form-grid {
    margin-bottom: 0.5rem;
}

.ai-edit-form-grid-full {
    grid-column: span 2;
}

.readonly-value {
    min-height: 42px;
    display: flex;
    align-items: center;
    padding: 0.65rem 0.8rem;
    border: 1px solid var(--color-border);
    border-radius: 8px;
    background: var(--color-bg);
    color: var(--color-text-muted);
}
/* Dismissals Table Layout */
.dismissal-pattern-cell {
    max-width: 0; /* Enable truncation in flex/grid/table context */
}

.pattern-text-wrapper {
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    font-family: inherit;
    font-size: 0.9rem;
    color: var(--color-text);
}

.muted-hint {
    color: var(--color-text-muted);
    font-size: 0.8rem;
    font-style: italic;
}

.text-right { text-align: right; }
.muted-text { color: var(--color-text-muted); }
.small-text { font-size: 0.85rem; }

.admin-table {
    width: 100%;
    border-collapse: collapse;
}

.admin-table th {
    text-align: left;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: var(--color-text-muted);
    padding: 0.75rem 1rem;
    border-bottom: 1px solid var(--color-border);
}

.admin-table td {
    padding: 0.875rem 1rem;
    border-bottom: 1px solid var(--color-border);
    vertical-align: middle;
}

.admin-table tr:last-child td {
    border-bottom: none;
}

.admin-table tr:hover td {
    background: rgba(255, 255, 255, 0.02);
}

.cursor-pointer {
    cursor: pointer;
}

.hover-accent:hover {
    color: var(--color-accent);
}

.provider-operations-tab {
    display: flex;
    flex-direction: column;
    gap: 1rem;
}

.provider-operations-stack {
    display: flex;
    flex-direction: column;
    gap: 1rem;
}

@media (max-width: 960px) {
    .provider-operations-stack {
        grid-template-columns: 1fr;
    }

    .ai-capability-row {
        grid-template-columns: 1fr 1fr;
    }

    .ai-capability-name {
        grid-column: 1 / -1;
    }
}

@media (max-width: 720px) {
    .ai-form-grid {
        grid-template-columns: 1fr;
    }

    .ai-form-grid .full-col,
    .ai-edit-form-grid-full,
    .ai-capability-panel {
        grid-column: span 1;
    }

    .ai-connection-item {
        flex-direction: column;
        align-items: stretch;
    }

    .ai-conn-actions {
        justify-content: flex-end;
        flex-wrap: wrap;
    }

    .ai-capability-row {
        grid-template-columns: 1fr;
    }
}
</style>
