// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Standard UI state contract for migrated screens and workflow segments.
 * Mirrors the UiState entity in `data-model.md`.
 */

export type UiStatus =
  | 'idle'
  | 'loading'
  | 'empty'
  | 'ready'
  | 'saving'
  | 'success'
  | 'validation_error'
  | 'error'
  | 'unauthorized'
  | 'disabled'

export interface UiAction {
  id: string
  label: string
  disabled?: boolean
}

export interface UiState<TData = unknown, TError = string> {
  status: UiStatus
  message?: string
  details?: string
  data?: TData
  error?: TError
  availableActions?: UiAction[]
  retryAction?: UiAction
}

export const idle = <T>(): UiState<T> => ({ status: 'idle' })
export const loading = <T>(message?: string): UiState<T> => ({ status: 'loading', message })
export const empty = <T>(message?: string): UiState<T> => ({ status: 'empty', message })
export const ready = <T>(data: T): UiState<T> => ({ status: 'ready', data })
export const saving = <T>(data?: T, message?: string): UiState<T> => ({ status: 'saving', data, message })
export const success = <T>(data: T, message?: string): UiState<T> => ({ status: 'success', data, message })
export const error = <T>(message: string, details?: string): UiState<T> => ({ status: 'error', error: message, message, details })
export const validationError = <T>(message: string, details?: string): UiState<T> => ({ status: 'validation_error', error: message, message, details })
export const unauthorized = <T>(message?: string): UiState<T> => ({ status: 'unauthorized', message })
export const disabled = <T>(message?: string): UiState<T> => ({ status: 'disabled', message })
