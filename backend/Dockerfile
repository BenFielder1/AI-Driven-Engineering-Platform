# syntax=docker/dockerfile:1
# AIDE API image: ASP.NET Core app + self-contained EF Core migration bundle (/app/efbundle).
# Build context: backend/   (docker build -f backend/Dockerfile backend)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Build-time OpenAPI generation (Microsoft.Extensions.ApiDescription.Server) is for the committed
# contract only; skip it in the image build. MSBuild reads this env var as a global property.
ENV OpenApiGenerateDocuments=false \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

# Restore layer: only the files that affect restore, so code edits don't bust the package cache.
COPY global.json Directory.Build.props Directory.Packages.props dotnet-tools.json ./
COPY src/Aide.Api/Aide.Api.csproj src/Aide.Api/
COPY src/Aide.Domain/Aide.Domain.csproj src/Aide.Domain/
COPY src/Aide.Infrastructure/Aide.Infrastructure.csproj src/Aide.Infrastructure/
RUN dotnet tool restore
RUN RID="linux-$(uname -m | sed 's/x86_64/x64/;s/aarch64/arm64/')" \
 && dotnet restore src/Aide.Api/Aide.Api.csproj \
 && dotnet restore src/Aide.Api/Aide.Api.csproj -r "$RID"

COPY src/ src/

RUN dotnet publish src/Aide.Api/Aide.Api.csproj -c Release --no-restore -o /app/publish

# Migration bundle for the node's architecture (uname -m in the build stage matches the local node).
# The DbContext tolerates a missing connection string at design time (EF.IsDesignTime); the Job passes --connection.
RUN RID="linux-$(uname -m | sed 's/x86_64/x64/;s/aarch64/arm64/')" \
 && dotnet ef migrations bundle --configuration Release --self-contained -r "$RID" \
      --project src/Aide.Infrastructure --startup-project src/Aide.Api -o /app/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# git: required by the Local Git provider and project working copies (/data/workspaces).
RUN apt-get update \
 && apt-get install -y --no-install-recommends git \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /app/efbundle /app/efbundle
# The pod runs with a read-only root FS; /tmp is a writable emptyDir. HOME=/tmp gives git (and .NET) a writable
# home for ~/.gitconfig; GIT_CONFIG_NOSYSTEM skips /etc/gitconfig so only the app's own config applies.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp \
    HOME=/tmp \
    GIT_CONFIG_NOSYSTEM=1
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Aide.Api.dll"]
