# Build stage
# mcr.microsoft.com/dotnet/sdk:10.0
FROM mcr.microsoft.com/dotnet/sdk@sha256:548d93f8a18a1acbe6cc127bc4f47281430d34a9e35c18afa80a8d6741c2adc3 AS build
WORKDIR /source

COPY MeisterProPR.slnx .
COPY src/ src/
COPY tests/ tests/

RUN dotnet restore

RUN dotnet publish src/MeisterProPR.Api/MeisterProPR.Api.csproj \
    -c Release -o /app --no-restore

# Tree-sitter native prune (feature 070, research.md R5). The TreeSitter.DotNet
# package ships ~28 grammar natives across 8 RIDs (~51 MB). The Api publish is
# framework-dependent, so the project's build/TreeSitterPrune.targets does not run
# here (it only fires on a self-contained publish of the library project). Prune at
# the image layer instead: keep the core library plus the 6 supported-language
# grammars on the two worker RIDs (linux-x64, linux-arm64) and drop every other RID.
RUN set -eux; \
    cd /app/runtimes 2>/dev/null || exit 0; \
    for rid in *; do \
        case "$rid" in linux-x64|linux-arm64) ;; *) rm -rf "$rid"; continue ;; esac; \
        find "$rid/native" -maxdepth 1 -name 'libtree-sitter-*.so' \
            ! -name 'libtree-sitter-typescript.so' \
            ! -name 'libtree-sitter-tsx.so' \
            ! -name 'libtree-sitter-javascript.so' \
            ! -name 'libtree-sitter-python.so' \
            ! -name 'libtree-sitter-go.so' \
            ! -name 'libtree-sitter-java.so' \
            ! -name 'libtree-sitter-ruby.so' \
            -delete 2>/dev/null || true; \
    done

RUN mkdir -p /app/.data-protection-keys

# Minimal Kerberos runtime slice for Azure DevOps client auth support.
# ubuntu:24.04
FROM ubuntu@sha256:786a8b558f7be160c6c8c4a54f9a57274f3b4fb1491cf65146521ae77ff1dc54 AS kerberos
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /kerberos-root/usr/lib/x86_64-linux-gnu \
        /kerberos-root/lib/x86_64-linux-gnu \
        /kerberos-root/etc/gss \
    && cp -a /usr/lib/x86_64-linux-gnu/libgssapi_krb5.so.2* /kerberos-root/usr/lib/x86_64-linux-gnu/ \
    && cp -a /lib/x86_64-linux-gnu/libkrb5.so.3* /kerberos-root/lib/x86_64-linux-gnu/ \
    && cp -a /lib/x86_64-linux-gnu/libk5crypto.so.3* /kerberos-root/lib/x86_64-linux-gnu/ \
    && cp -a /lib/x86_64-linux-gnu/libkrb5support.so.0* /kerberos-root/lib/x86_64-linux-gnu/ \
    && cp -a /lib/x86_64-linux-gnu/libcom_err.so.2* /kerberos-root/lib/x86_64-linux-gnu/ \
    && cp -a /lib/x86_64-linux-gnu/libkeyutils.so.1* /kerberos-root/lib/x86_64-linux-gnu/ \
    && cp -a /etc/gss /kerberos-root/etc/

# Minimal git runtime slice. The reviewer fetches PR repositories with the git
# CLI (bare mirror + worktrees) instead of SCM REST APIs, which the chiseled
# runtime image does not ship. Rather than fall back to the full Debian aspnet
# image (which carries ~170 OS CVEs), stage just the git binary, its git-core
# helpers, and their shared-library closure into the chiseled runtime — the same
# approach used for Kerberos above. Ubuntu 24.04 (Noble) matches the chiseled
# base's glibc, so the copied libraries are ABI-compatible. perl is intentionally
# excluded: the fetch/worktree/rev-parse plumbing never invokes perl subcommands.
# ubuntu:24.04
FROM ubuntu@sha256:786a8b558f7be160c6c8c4a54f9a57274f3b4fb1491cf65146521ae77ff1dc54 AS gittools
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN set -eux; \
    mkdir -p /git-root/usr/bin /git-root/usr/lib/git-core /git-root/etc/ssl/certs; \
    cp -a /usr/bin/git /git-root/usr/bin/git; \
    # `true` for GIT_ASKPASS=/bin/true (chiseled ships no coreutils). Only stage
    # /usr/bin/true: the chiseled base is merged-/usr, so /bin is a symlink to
    # /usr/bin and /bin/true resolves there. Staging a real /git-root/bin
    # directory would instead collide with that symlink on COPY.
    cp -a /usr/bin/true /git-root/usr/bin/true; \
    cp -a /usr/lib/git-core/. /git-root/usr/lib/git-core/; \
    cp -aL /etc/ssl/certs/ca-certificates.crt /git-root/etc/ssl/certs/ca-certificates.crt; \
    # Copy the shared-library closure of git + its git-core ELF helpers, excluding
    # the glibc/libgcc/libstdc++ runtime already provided by the chiseled base
    # (overwriting those could break the bundled .NET runtime). Canonicalize each
    # lib's *directory* to its real /usr/lib path (Ubuntu is merged-/usr, so ldd
    # often reports /lib/... aliases) while keeping the SONAME basename, so the
    # staged tree has no top-level /lib that would collide with the chiseled
    # base's /lib -> usr/lib symlink during COPY.
    for bin in /usr/bin/git $(find /usr/lib/git-core -maxdepth 1 -type f -perm -u+x); do \
        ldd "$bin" 2>/dev/null | awk '/=>/ {print $3}'; \
    done | sort -u | grep -E '^/' \
       | grep -Ev 'ld-linux|/libc\.so|/libc-|/libm\.so|/libm-|/libdl\.so|/libpthread\.so|/librt\.so|/libresolv|/libstdc\+\+\.so|/libgcc_s\.so|/libnss_' \
       | while read -r lib; do \
            cp -aL --parents "$(readlink -f "$(dirname "$lib")")/$(basename "$lib")" /git-root/; \
         done

# Runtime stage
# mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
FROM mcr.microsoft.com/dotnet/aspnet@sha256:de3e2d510c3b30dd10a3ababad927725839aacd0bbd6a3e8aef9a5a4408ccc12 AS runtime
WORKDIR /app

COPY --from=kerberos /kerberos-root/usr/lib/x86_64-linux-gnu/ /usr/lib/x86_64-linux-gnu/
COPY --from=kerberos /kerberos-root/lib/x86_64-linux-gnu/ /lib/x86_64-linux-gnu/
COPY --from=kerberos /kerberos-root/etc/gss/ /etc/gss/
COPY --from=gittools /git-root/ /
COPY --from=build --chown=1654:1654 --chmod=755 /app /app

ENV GIT_EXEC_PATH=/usr/lib/git-core

USER app

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "MeisterProPR.Api.dll", "--healthcheck", "http://127.0.0.1:8080/healthz"]
ENTRYPOINT ["dotnet", "MeisterProPR.Api.dll"]
