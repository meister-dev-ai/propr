# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS build
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
FROM ubuntu:26.04@sha256:6a06111c3b0a8600feadb270aec771dc239bf407b25625f93f44ab00e11f4d3c AS kerberos
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:a9298ccc39c6258ae71228479e2f3f8d2ceb6639a9927262a4b0ff7d319a0e7e AS runtime
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
