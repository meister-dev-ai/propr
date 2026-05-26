# Frontend

This app is the active Vuetify-based frontend for Meister ProPR and is developed in `frontend/`.

## Runtime Modes

- Mock mode: runs the app with `VITE_MOCK=true`, starts MSW before mount, and uses representative fixture-backed handlers.
- Live mode: runs the app against the Vite `/api` proxy, which forwards requests to the local backend on `http://localhost:8080`.

## Commands

Install dependencies:

```bash
npm install
```

Run mock mode:

```bash
npm run mock
```

Run live mode:

```bash
npm run dev
```

Regenerate OpenAPI client types after contract changes:

```bash
npm run generate:api
```

Run unit tests:

```bash
npm test
```

Run type-checking:

```bash
npm run type-check
```

Run Playwright E2E:

```bash
npm run test:e2e
```

Run production build validation:

```bash
npm run build
```

## E2E Parity Notes

- The Playwright config runs separate `mock` and `live` projects.
- The `mock` project starts `npm run mock`.
- The `live` project starts `npm run dev` with `VITE_API_BASE_URL=/api`.
- Focused live-mode parity tests intercept only the exercised `/api/**` requests in Playwright so the same workflows can run deterministically without a full backend.

## Key Files

- `src/main.ts`: runtime bootstrap and mock-worker startup.
- `src/app/runtime/`: runtime mode creation and active-runtime context.
- `src/mocks/handlers.ts`: representative MSW handlers and mock licensing state.
- `tests/e2e/runtimeParity.ts`: live-mode parity interception helpers.
