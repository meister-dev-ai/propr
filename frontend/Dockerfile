# Build context: repository root (example/docker-compose sets context: ../..)
# Stage 1: Build
FROM node:22-alpine@sha256:9385cd9f3001dfc3431e8ead12c43e9e1f87cc1b9b5c6cfd0f73865d405b27c4 AS builder
WORKDIR /app
# Copy openapi.json from repo root for code generation
COPY openapi.json ./openapi.json
# Install deps from frontend/
COPY frontend/package*.json ./
RUN npm ci
# Copy source
COPY frontend/ .
# Generate typed API client then build
# openapi.json was copied to /app/openapi.json; pass the path explicitly so it
# works regardless of whether the script uses ../ (local dev) or ./ (Docker)
RUN npx openapi-typescript ./openapi.json -o src/services/generated/openapi.ts
RUN npm run build

# Stage 2: Serve
FROM docker.io/library/nginx:alpine@sha256:8b1e78743a03dbb2c95171cc58639fef29abc8816598e27fb910ed2e621e589a
COPY frontend/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=builder /app/dist /usr/share/nginx/html
EXPOSE 80
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD ["nginx", "-t"]
CMD ["nginx", "-g", "daemon off;"]
