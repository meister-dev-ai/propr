// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Base view-model contract used by feature-specific view models.
 * Mirrors the ViewModel entity in `data-model.md` (state, actions, errorMapping).
 *
 * View models own data loading, mutation orchestration, validation preparation, and
 * user-facing error mapping. They must NOT render markup — that is the view's job.
 */

import type { Ref, ComputedRef } from 'vue'
import type { UiState } from './uiState'

export interface ViewModelState<TData = unknown, TError = string> {
  state: Ref<UiState<TData, TError>> | ComputedRef<UiState<TData, TError>>
}

export interface ViewModelActions {
  /** Re-runs the primary data load. Required when the screen depends on remote data. */
  refresh?: () => Promise<void> | void
  /** Resets the screen to its idle state. Useful for dialogs and ephemeral flows. */
  reset?: () => void
}

export interface ViewModel<TData = unknown, TError = string>
  extends ViewModelState<TData, TError>, ViewModelActions {
  /**
   * Human-readable name of the view model, used for diagnostics and parity-evidence
   * cross-references.
   */
  readonly name: string
}

export interface ServiceError {
  code?: string
  message: string
  details?: string
  status?: number
}

export type ErrorMapper<TServiceError = ServiceError> = (raw: TServiceError) => string
