# Multi-arch image (linux/amd64, linux/arm64). Framework-dependent on Microsoft's chiseled ASP.NET image: no shell, no
# package manager, runs as a non-root user, so few CVEs; a base-image patch is picked up by rebuilding.
ARG DOTNET_VERSION=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
ARG VERSION=0.1.0
WORKDIR /src
COPY src/GmmPanel/GmmPanel.csproj src/GmmPanel/
RUN dotnet restore src/GmmPanel/GmmPanel.csproj -a $TARGETARCH
COPY src/ src/
RUN dotnet publish src/GmmPanel/GmmPanel.csproj -c Release -a $TARGETARCH --no-restore --self-contained false \
        -p:Version=$VERSION -o /app \
    && mkdir -p /data

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled
WORKDIR /app
COPY --from=build /app .
# A named volume copies this empty, app-owned folder on first use, so the non-root user can write to it
COPY --from=build --chown=1654:1654 /data /data
ENV Urls=http://0.0.0.0:8080 \
    Panel__DataDirectory=/data \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
VOLUME /data
USER 1654
ENTRYPOINT ["dotnet", "gmmpanel.dll"]
