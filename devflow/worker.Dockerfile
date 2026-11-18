# syntax=docker/dockerfile:1
# The worker's base image, built from the repository root, which is the context every image shares:
#   docker build -f devflow/worker.Dockerfile .
# infra/hosted/compose.yaml builds it at every release, as the service devflow-worker, and never
# starts it. A project's worker is built FROM it by the project's own .devflow/Dockerfile, which the
# command on the project's page fetches and builds; see "Starting a worker" and "The base image" in
# devflow/README.md, and "Workers" in devflow/docs/devflow-architecture.md.
#
# Two users: the worker program runs as root, the entrypoint, and keeps a job's secrets in
# /run/devflow/, which only root can read, and the bundles it hands the agent in
# /run/devflow-handover/, which only root can write; agent, uid 1000, runs Claude Code, the
# project's check and its start, started by the worker through setpriv, and owns the working copies
# under /work.

# --- Build: the worker program, one self-contained file for the build's architecture. ---
# The SDK devflow/global.json pins, 10.0.4xx, restoring from the committed packages.lock.json.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY devflow/global.json devflow/Directory.Build.props devflow/Directory.Packages.props devflow/.editorconfig devflow/
COPY devflow/src/DevFlow.Protocol devflow/src/DevFlow.Protocol
COPY devflow/src/DevFlow.Worker devflow/src/DevFlow.Worker
WORKDIR /src/devflow
# The runtime identifier for the architecture the image is built for, of the two the worker's
# project lists. sharing=locked, as in devflow/Dockerfile: NuGet's package folder is not safe for
# two restores at once, which compose's parallel builds would otherwise run.
RUN case "$TARGETARCH" in \
      arm64) echo linux-arm64 > /rid ;; \
      amd64) echo linux-x64 > /rid ;; \
      *) echo "the worker is published for arm64 and amd64, not $TARGETARCH" >&2 && exit 1 ;; \
    esac
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/DevFlow.Worker/DevFlow.Worker.csproj --locked-mode && \
    dotnet publish src/DevFlow.Worker/DevFlow.Worker.csproj -c Release --no-restore -r "$(cat /rid)" -o /out

# --- Run: Node, git and Claude Code, the worker as root and the agent as agent. ---
FROM node:26-trixie-slim

# util-linux brings setpriv, which the worker starts the agent's processes with; procps brings ps.
RUN apt-get update && \
    apt-get install -y --no-install-recommends git ca-certificates jq procps util-linux && \
    rm -rf /var/lib/apt/lists/*

# The image's user with uid 1000, node, becomes agent, with its home /home/agent, so uid 1000 is
# the agent's and nothing else's.
RUN usermod --login agent --home /home/agent --move-home node && \
    groupmod --new-name agent node

# Claude Code, at the version devflow/worker/package-lock.json pins with its integrity, installed
# with npm ci like every npm install in an image, and on PATH for every user.
WORKDIR /opt/devflow-tools
COPY devflow/worker/package.json devflow/worker/package-lock.json ./
RUN --mount=type=cache,target=/root/.npm npm ci --omit=dev && \
    ln -s /opt/devflow-tools/node_modules/.bin/claude /usr/local/bin/claude

# Claude Code adds a Co-Authored-By trailer to commits and a line to pull requests unless told
# not to; attribution with both empty turns them off, see "The base image" in devflow/README.md.
RUN mkdir -p /home/agent/.claude && \
    printf '%s\n' '{"attribution": {"commit": "", "pr": "", "sessionUrl": false}}' > /home/agent/.claude/settings.json && \
    chown -R agent:agent /home/agent/.claude

# The job's secrets, root's alone; the bundles root hands the agent, root's, which the agent reads
# and cannot write; the working copies, the agent's.
RUN install -d -m 700 -o root -g root /run/devflow && \
    install -d -m 755 -o root -g root /run/devflow-handover && \
    install -d -m 755 -o agent -g agent /work

COPY --chmod=755 --from=build /out/DevFlow.Worker /usr/local/bin/devflow-worker

# What the worker reports in hello, from the build of a project's image FROM this one: the command
# on the project's page passes the base image's name and the recipe's version, the commit Dev flow
# read the recipe at, as build arguments, and these turn them into the variables the worker reads.
ONBUILD ARG DEVFLOW_BASE
ONBUILD ARG DEVFLOW_RECIPE_VERSION
ONBUILD ENV DEVFLOW_BASE_IMAGE=${DEVFLOW_BASE} DEVFLOW_RECIPE_VERSION=${DEVFLOW_RECIPE_VERSION}

WORKDIR /work
# The worker runs as root, so it can keep /run/devflow/ from the agent; see the top of this file.
ENTRYPOINT ["/usr/local/bin/devflow-worker"]
