# syntax=docker/dockerfile:1
# One Dockerfile, two runnable targets:
#   docker build --target api       -t aegisbank-api .
#   docker build --target dashboard -t aegisbank-dashboard .

# ---------------------------------------------------------------- native engine
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS native
RUN apt-get update \
 && apt-get install -y --no-install-recommends g++ cmake ninja-build libgtest-dev \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /engine
COPY src/AegisBank.Engine/ .
# The image build fails if any native unit test fails.
RUN cmake -S . -B /engine-build -G Ninja -DCMAKE_BUILD_TYPE=Release \
 && cmake --build /engine-build \
 && ctest --test-dir /engine-build --output-on-failure

# ---------------------------------------------------------------- managed build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props ./
COPY src/AegisBank.Domain/AegisBank.Domain.csproj src/AegisBank.Domain/
COPY src/AegisBank.Application/AegisBank.Application.csproj src/AegisBank.Application/
COPY src/AegisBank.Infrastructure/AegisBank.Infrastructure.csproj src/AegisBank.Infrastructure/
COPY src/AegisBank.Api/AegisBank.Api.csproj src/AegisBank.Api/
COPY src/AegisBank.Dashboard/AegisBank.Dashboard.csproj src/AegisBank.Dashboard/
RUN dotnet restore src/AegisBank.Api/AegisBank.Api.csproj \
 && dotnet restore src/AegisBank.Dashboard/AegisBank.Dashboard.csproj

COPY src/AegisBank.Domain/ src/AegisBank.Domain/
COPY src/AegisBank.Application/ src/AegisBank.Application/
COPY src/AegisBank.Infrastructure/ src/AegisBank.Infrastructure/
COPY src/AegisBank.Api/ src/AegisBank.Api/
COPY src/AegisBank.Dashboard/ src/AegisBank.Dashboard/
# The .NET projects pick the engine up from artifacts/native/ and copy it next to their binaries.
COPY --from=native /engine-build/libaegis_engine.so artifacts/native/libaegis_engine.so

RUN dotnet publish src/AegisBank.Api/AegisBank.Api.csproj -c Release -o /out/api --no-restore \
 && dotnet publish src/AegisBank.Dashboard/AegisBank.Dashboard.csproj -c Release -o /out/dashboard --no-restore \
 && test -f /out/api/libaegis_engine.so && test -f /out/dashboard/libaegis_engine.so

# ---------------------------------------------------------------- runtime base
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
EXPOSE 8080
# Probe without curl/wget: the base image ships neither.
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /health/ready HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3 && head -1 <&3 | grep -q " 200 "'

# ---------------------------------------------------------------- API
FROM runtime AS api
WORKDIR /app
COPY --from=build /out/api .
USER app
ENTRYPOINT ["dotnet", "AegisBank.Api.dll"]

# ---------------------------------------------------------------- dashboard
FROM runtime AS dashboard
WORKDIR /app
COPY --from=build /out/dashboard .
# Directory for cookie-encryption keys; a named volume mounted here inherits this ownership.
RUN mkdir -p /keys && chown app:app /keys
USER app
ENTRYPOINT ["dotnet", "AegisBank.Dashboard.dll"]
