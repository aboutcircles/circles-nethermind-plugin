FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Detect target architecture (amd64/arm64)
ARG TARGETARCH
RUN echo "Building for architecture: ${TARGETARCH}"

# Copy all necessary project files for dependency resolution
COPY src/Pathfinder/Circles.Pathfinder/Circles.Pathfinder.csproj ./Circles.Pathfinder/
COPY src/Pathfinder/Circles.Pathfinder.Host/Circles.Pathfinder.Host.csproj ./Circles.Pathfinder.Host/

# Restore just the target project - this will automatically restore its dependencies
RUN dotnet restore Circles.Pathfinder.Host/Circles.Pathfinder.Host.csproj

# Copy all source code
COPY ./src/Pathfinder/ .
WORKDIR /src/Circles.Pathfinder.Host

# Build and publish the project for the target architecture
RUN if [ "$TARGETARCH" = "arm64" ]; then \
        dotnet publish -c Release -r linux-arm64 -o /app/publish; \
    else \
        dotnet publish -c Release -r linux-x64 -o /app/publish; \
    fi

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["./Circles.Pathfinder.Host"]