# Circles Cache Service Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy entire Index directory (needed for project references)
COPY src/Index ./Index

# Copy Common directory (Circles.Common shared types)
COPY src/Common ./Common

# Copy Cache Service project
COPY src/Cache ./Cache

# Restore dependencies
RUN dotnet restore ./Cache/Circles.Cache.Service/Circles.Cache.Service.csproj

# Build and publish
WORKDIR /src/Cache/Circles.Cache.Service
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Install curl for health checks and psql for manual DB operations
RUN apt-get update && apt-get install -y curl postgresql-client && rm -rf /var/lib/apt/lists/*

# Copy published app
COPY --from=build /app/publish .

# Set environment variables
ENV ASPNETCORE_URLS=http://+:3001 \
    ASPNETCORE_ENVIRONMENT=Production

# Expose port
EXPOSE 3001

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=60s --retries=10 \
    CMD curl -f http://localhost:3001/ready || exit 1

ENTRYPOINT ["dotnet", "Circles.Cache.Service.dll"]
