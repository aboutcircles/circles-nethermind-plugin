FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Detect target architecture (amd64/arm64)
ARG TARGETARCH
RUN echo "Building for architecture: ${TARGETARCH}"

# Copy Index, Pathfinder, and RPC sources (required for local project references)
# RPC Host specifically depends on:
# - Circles.Index.Common
# - Circles.Index.Query
# - Circles.Index.DatabaseSchemaProvider
# Pathfinder depends on:
# - Circles.Index.Common
# We copy the entire Index folder since Docker COPY works at directory level
# and DatabaseSchemaProvider transitively depends on other Index projects
COPY ./src/Index ./Index
COPY ./src/Pathfinder ./Pathfinder
COPY ./src/Rpc ./Rpc

# Restore RPC host and its dependencies
RUN dotnet restore ./Rpc/Circles.Rpc.Host/Circles.Rpc.Host.csproj

# Publish RPC host
# Note: Now uses local project references instead of NuGet packages
WORKDIR /src/Rpc/Circles.Rpc.Host
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["./Circles.Rpc.Host"]
