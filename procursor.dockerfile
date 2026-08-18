# Build stage
# mcr.microsoft.com/dotnet/sdk:10.0
FROM mcr.microsoft.com/dotnet/sdk@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
WORKDIR /source

COPY src/ src/
COPY tests/ tests/

RUN dotnet restore src/MeisterDev.ProPR.ProCursor.Service/MeisterDev.ProPR.ProCursor.Service.csproj

RUN dotnet publish src/MeisterDev.ProPR.ProCursor.Service/MeisterDev.ProPR.ProCursor.Service.csproj \
    -c Release -o /app --no-restore

RUN mkdir -p /app/.data-protection-keys

# Minimal Kerberos runtime slice for Azure DevOps client auth support.
# ubuntu:24.04
FROM ubuntu@sha256:561618e2c15bf2397621dd04f96926663a3b5616c189cf7e38db7e82f5c538ea AS kerberos
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

# Runtime stage
# mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
FROM mcr.microsoft.com/dotnet/aspnet@sha256:f5b3b2e2e548828d50e349726f51a5de001286f02c4bbde77db0dd34eb9f55ff AS runtime
WORKDIR /app

COPY --from=kerberos /kerberos-root/usr/lib/x86_64-linux-gnu/ /usr/lib/x86_64-linux-gnu/
COPY --from=kerberos /kerberos-root/lib/x86_64-linux-gnu/ /lib/x86_64-linux-gnu/
COPY --from=kerberos /kerberos-root/etc/gss/ /etc/gss/
COPY --from=build --chown=1654:1654 --chmod=755 /app /app

USER app

EXPOSE 8081
ENV ASPNETCORE_URLS=http://+:8081
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "MeisterDev.ProPR.ProCursor.Service.dll", "--healthcheck", "http://127.0.0.1:8081/healthz"]
ENTRYPOINT ["dotnet", "MeisterDev.ProPR.ProCursor.Service.dll"]
