# Circles Metrics Exporter Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy shared build props (defines TFM)
COPY Directory.Build.props ./

# Copy Metrics Exporter project and its dependencies
COPY src/Metrics ./Metrics
COPY src/Common/Circles.Common ./Common/Circles.Common

# Restore dependencies
RUN dotnet restore ./Metrics/Circles.Metrics.Exporter/Circles.Metrics.Exporter.csproj

# Build and publish
WORKDIR /src/Metrics/Circles.Metrics.Exporter
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install curl for health checks and psql for manual DB operations
RUN apt-get update && apt-get install -y curl postgresql-client && rm -rf /var/lib/apt/lists/*

# Create non-root user with fixed UID (consistent across all circles services)
RUN groupadd -g 10000 circles && useradd -u 10000 -g circles -s /sbin/nologin circles

# Copy published app
COPY --from=build /app/publish .

# Set environment variables
ENV ASPNETCORE_URLS=http://+:9100 \
    ASPNETCORE_ENVIRONMENT=Production

# Expose port
EXPOSE 9100

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:9100/ready || exit 1

USER circles
ENTRYPOINT ["dotnet", "Circles.Metrics.Exporter.dll"]
