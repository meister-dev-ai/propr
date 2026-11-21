# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4 AS build
WORKDIR /source

# The repository-root build properties, so the image restores the same package versions a local build
# resolves.
COPY Directory.Build.props .
COPY src/ src/
COPY tests/ tests/

RUN dotnet restore src/MeisterDev.ProPR.ProCursor.Service/MeisterDev.ProPR.ProCursor.Service.csproj
RUN dotnet restore src/MeisterDev.DataProtection.AzureKeyVaultAddIn/MeisterDev.DataProtection.AzureKeyVaultAddIn.csproj

RUN dotnet publish src/MeisterDev.ProPR.ProCursor.Service/MeisterDev.ProPR.ProCursor.Service.csproj \
    -c Release -o /app --no-restore

# The key-ring protector add-in directory, at the same path the API image uses. Both services read the same
# key ring, so both need the protector that opens it.
RUN dotnet publish src/MeisterDev.DataProtection.AzureKeyVaultAddIn/MeisterDev.DataProtection.AzureKeyVaultAddIn.csproj \
    -c Release -o /app/data-protection-add-ins/MeisterDev.DataProtection.AzureKeyVaultAddIn --no-restore

RUN mkdir -p /app/.data-protection-keys

# Minimal Kerberos runtime slice for Azure DevOps client auth support.
FROM ubuntu:24.04@sha256:9c7f200c07e3d4a03e7249d2c16bf66a51120fb876aa2242cf31451412543289 AS kerberos
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:cae5b962bfb786606def03d250c6a645cc98839095c89e806e4bd8978a68115b AS runtime
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
