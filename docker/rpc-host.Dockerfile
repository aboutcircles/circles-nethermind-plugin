FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Detect target architecture (amd64/arm64)
ARG TARGETARCH
RUN echo "Building for architecture: ${TARGETARCH}"

# Copy Nethermind sources (needed to build / include Nethermind.Core)
COPY ./src/nethermind ./nethermind
WORKDIR /src/nethermind
# Build Nethermind.Core so the DLL is available for the RPC host
# RUN ./scripts/build/build.sh -c Release
RUN dotnet build src/Nethermind/Nethermind.Core/Nethermind.Core.csproj -c Release
RUN dotnet build src/Nethermind/Nethermind.Logging/Nethermind.Logging.csproj -c Release

WORKDIR /src
# Copy RPC sources and restore (project path must match publish path)
COPY ./src/Rpc ./Rpc
RUN dotnet restore ./Rpc/Circles.Rpc.Host/Circles.Rpc.Host.csproj

# RUN dotnet build nethermind/src/Nethermind/Nethermind.Core/Nethermind.Core.csproj -c Release -o /src/nethermind-core-out

# Publish RPC host (self-contained with architecture-specific RID)
WORKDIR /src/Rpc/Circles.Rpc.Host
RUN dotnet publish \
    -c Release \
    -r linux-$([ "$TARGETARCH" = "x64" ] && echo "arm64" || echo "x64") \
    -o /app/publish \
    --no-restore
RUN cp /src/nethermind/src/Nethermind/Nethermind.Core/bin/Release/net9.0/*/Nethermind.Core.dll /app/publish/ && \
    cp /src/nethermind/src/Nethermind/Nethermind.Logging/bin/Release/net9.0/*/Nethermind.Logging.dll /app/publish/

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["./Circles.Rpc.Host"]
