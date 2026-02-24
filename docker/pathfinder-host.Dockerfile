FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Detect target architecture (amd64/arm64)
ARG TARGETARCH
RUN echo "Building for architecture: ${TARGETARCH}"

# Copy shared build props (defines TFM)
COPY Directory.Build.props ./

# Copy Index, Common, and Pathfinder sources (Pathfinder depends on Circles.Common)
COPY ./src/Index ./Index
COPY ./src/Common ./Common
COPY ./src/Pathfinder ./Pathfinder

# Restore Pathfinder host and its dependencies
RUN dotnet restore ./Pathfinder/Circles.Pathfinder.Host/Circles.Pathfinder.Host.csproj

# Build and publish the project
WORKDIR /src/Pathfinder/Circles.Pathfinder.Host
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Install psql for manual DB operations
RUN apt-get update && apt-get install -y postgresql-client && rm -rf /var/lib/apt/lists/*

# Create non-root user with fixed UID (consistent across all circles services)
RUN groupadd -g 10000 circles && useradd -u 10000 -g circles -s /sbin/nologin circles

COPY --from=build /app/publish .

USER circles
ENTRYPOINT ["./Circles.Pathfinder.Host"]