FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Detect target architecture (amd64/arm64)
ARG TARGETARCH
RUN echo "Building for architecture: ${TARGETARCH}"

# Copy Nethermind sources (needed to build / include Nethermind.Core)
COPY ./src/nethermind ./nethermind
WORKDIR /src/nethermind
# Build Nethermind assemblies so we get runtime DLLs (not reference assemblies)
# Using build instead of publish to get proper runtime assemblies
RUN dotnet build src/Nethermind/Nethermind.Core/Nethermind.Core.csproj -c Release -o /nethermind-libs
RUN dotnet build src/Nethermind/Nethermind.Logging/Nethermind.Logging.csproj -c Release -o /nethermind-libs

WORKDIR /src
# Copy RPC sources and restore (project path must match publish path)
COPY ./src/Rpc ./Rpc
RUN dotnet restore ./Rpc/Circles.Rpc.Host/Circles.Rpc.Host.csproj

# RUN dotnet build nethermind/src/Nethermind/Nethermind.Core/Nethermind.Core.csproj -c Release -o /src/nethermind-core-out

# Publish RPC host
WORKDIR /src/Rpc/Circles.Rpc.Host
RUN dotnet publish -c Release -o /app/publish --no-restore

# Copy Nethermind runtime DLLs from the publish output
RUN cp /nethermind-libs/Nethermind.Core.dll /app/publish/ && \
    cp /nethermind-libs/Nethermind.Logging.dll /app/publish/

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["./Circles.Rpc.Host"]
