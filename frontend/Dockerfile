# Build context: repository root (example/docker-compose sets context: ../..)
# Stage 1: Build
FROM node:26-alpine@sha256:40cd011b55415bc894e45b02fdd5a5dfde056921b7d23a64c74f80497af48797 AS builder
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
# The slim variant omits the image-filter/dav/xslt/geoip modules and their
# native library closure (libtiff, freetype, libgd, nghttp2). Serving the built
# SPA needs none of them, and dropping them clears the libtiff CVEs (no Alpine
# fix available) that the full alpine tag carries.
FROM docker.io/library/nginx:alpine-slim@sha256:ebd058041a732a2b2dc0b606e34572dd8e0034de6a4aa4d50d8c13a240ac6005
# Apply available security fixes to the TLS, regular-expression and compression
# libraries used by nginx. The zlib minimum prevents a failed repository refresh
# from retaining a vulnerable installed version.
RUN apk add --no-cache --upgrade libssl3 libcrypto3 pcre2 'zlib>=1.3.2-r1'
COPY frontend/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=builder /app/dist /usr/share/nginx/html
# Run as the unprivileged nginx user (uid 101, already present in the base image)
# instead of root. Binding a privileged port as non-root is not possible, so
# nginx.conf listens on 8080 (matching the backend's non-root/8080 convention),
# and the pid file plus cache/temp dirs are made writable by the nginx user.
RUN chown -R nginx:nginx /var/cache/nginx /usr/share/nginx/html \
    && touch /run/nginx.pid \
    && chown nginx:nginx /run/nginx.pid
USER nginx
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD ["nginx", "-t"]
CMD ["nginx", "-g", "daemon off;"]
