FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Detect target architecture (amd64/arm64)
ARG TARGETARCH
RUN echo "Building for architecture: ${TARGETARCH}"

# Copy RPC sources and restore
COPY ./src/Rpc ./Rpc
RUN dotnet restore ./Rpc/Circles.Rpc.Host/Circles.Rpc.Host.csproj

# Publish RPC host
# Note: Nethermind assemblies now come from NuGet packages (Gnosis.Circles.Nethermind.Plugin.*)
# No need to build Nethermind from source anymore
WORKDIR /src/Rpc/Circles.Rpc.Host
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["./Circles.Rpc.Host"]
