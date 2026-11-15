# syntax=docker/dockerfile:1
# Dev flow's image, built from the repository root, which is the context every image shares:
#   docker build -f devflow/Dockerfile .
# infra/hosted/compose.yaml builds it and sets everything it reads, see infra/hosted/README.md.
# Written like review/Dockerfile, without the migrations Dev flow does not have yet.

# --- Build: Dev flow's page, as .github/workflows/web.yml builds it. ---
# design/ui is installed first, since the page installs @platform/ui from it by path.
FROM node:26-trixie-slim AS web
WORKDIR /src
COPY design/ui/package.json design/ui/package-lock.json design/ui/
RUN --mount=type=cache,target=/root/.npm cd design/ui && npm ci
COPY design/ui design/ui
COPY devflow/web/package.json devflow/web/package-lock.json devflow/web/
RUN --mount=type=cache,target=/root/.npm cd devflow/web && npm ci
COPY devflow/web devflow/web
RUN cd devflow/web && npm run build

# --- Build: the published app, from the committed packages.lock.json files. ---
# The SDK devflow/global.json pins, 10.0.4xx.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY libs/dotnet/PlatformAuth libs/dotnet/PlatformAuth
COPY devflow/global.json devflow/Directory.Build.props devflow/Directory.Packages.props devflow/.editorconfig devflow/
COPY devflow/src/DevFlow devflow/src/DevFlow
WORKDIR /src/devflow
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore src/DevFlow/DevFlow.csproj --locked-mode && \
    dotnet publish src/DevFlow/DevFlow.csproj -c Release --no-restore -o /out

# --- Run: the app and its page, as the image's own user that is not root, app. ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --chmod=a+rX --from=build /out /app
# Dev flow serves web/dist from its working directory.
COPY --chmod=a+rX --from=web /src/devflow/web/dist /app/web/dist
USER app
ENTRYPOINT ["dotnet", "DevFlow.dll"]
