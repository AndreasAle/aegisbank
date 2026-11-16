# Toolchain image for hosts that have Docker but no local .NET SDK / C++ compiler.
# Usage is documented in README.md ("Building without a local toolchain").
FROM mcr.microsoft.com/dotnet/sdk:10.0

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      g++ cmake ninja-build libgtest-dev rsync postgresql-client \
 && rm -rf /var/lib/apt/lists/*

RUN dotnet tool install --tool-path /usr/local/bin dotnet-ef --version "10.0.*"

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
WORKDIR /work
