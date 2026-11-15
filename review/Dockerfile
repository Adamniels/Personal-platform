# syntax=docker/dockerfile:1
# Review's image, built from the repository root, which is the context every image shares:
#   docker build -f review/Dockerfile .
# infra/hosted/compose.yaml builds it, runs its migrations and sets everything it reads, see
# infra/hosted/README.md.

# --- Build: review's page, as .github/workflows/web.yml builds it. ---
# design/ui is installed first, since the page installs @platform/ui from it by path.
FROM node:26-trixie-slim AS web
WORKDIR /src
COPY design/ui/package.json design/ui/package-lock.json design/ui/
RUN --mount=type=cache,target=/root/.npm cd design/ui && npm ci
COPY design/ui design/ui
COPY review/web/package.json review/web/package-lock.json review/web/
RUN --mount=type=cache,target=/root/.npm cd review/web && npm ci
COPY review/web review/web
RUN cd review/web && npm run build

# --- Build: the published app, from the committed packages.lock.json files. ---
# The SDK review/global.json pins, 10.0.4xx; the migrations are embedded in the assembly.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY libs/dotnet/PlatformAuth libs/dotnet/PlatformAuth
COPY libs/dotnet/PlatformData libs/dotnet/PlatformData
COPY review/global.json review/Directory.Build.props review/Directory.Packages.props review/.editorconfig review/
COPY review/migrations review/migrations
COPY review/src/Review review/src/Review
WORKDIR /src/review
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore src/Review/Review.csproj --locked-mode && \
    dotnet publish src/Review/Review.csproj -c Release --no-restore -o /out

# --- Run: the app and its page, as the image's own user that is not root, app. ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --chmod=a+rX --from=build /out /app
# Review serves web/dist from its working directory.
COPY --chmod=a+rX --from=web /src/review/web/dist /app/web/dist
USER app
# Serves with no arguments; `migrate` as the command.
ENTRYPOINT ["dotnet", "Review.dll"]
