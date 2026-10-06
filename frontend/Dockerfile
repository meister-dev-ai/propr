# Build context: repository root (example/docker-compose sets context: ../..)
# Stage 1: Build
# node:26-alpine
FROM node@sha256:ef24c5053d50fdc3e4e56eb4e7ddb7861874ab0fdc797046ba897581deb8e868 AS builder
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
# docker.io/library/nginx:alpine-slim
# The slim variant omits the image-filter/dav/xslt/geoip modules and their
# native library closure (libtiff, freetype, libgd, nghttp2). Serving the built
# SPA needs none of them, and dropping them clears the libtiff CVEs (no Alpine
# fix available) that the full alpine tag carries.
FROM docker.io/library/nginx@sha256:f761b94f2cb9e8e05e2943d5f773609596113ef69b54e2433a996d109a8f78b7
# Alpine 3.24 ships openssl 3.5.9 and pcre2 10.49 with the current security
# fixes, and the pinned nginx image still carries 3.5.8 and 10.48. This step
# installs the fixed versions before nginx:alpine-slim is rebuilt. Remove it
# once the pinned digest ships these versions itself.
RUN apk add --no-cache --upgrade libssl3 libcrypto3 pcre2
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
