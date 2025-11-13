FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Detect target architecture
ARG TARGETARCH
RUN echo "Building for architecture: ${TARGETARCH}"

# use dotnet related caching
COPY src/Pathfinder/Circles.Pathfinder/Circles.Pathfinder.csproj ./Circles.Pathfinder/
COPY src/Pathfinder/Circles.Pathfinder.Host/Circles.Pathfinder.Host.csproj ./Circles.Pathfinder.Host/
RUN dotnet restore ./Circles.Pathfinder.Host/Circles.Pathfinder.Host.csproj

# Copy all source code
COPY ./src/Pathfinder .
WORKDIR /src/Circles.Pathfinder.Host
RUN dotnet publish \
    -c Release \
    -r linux-$([ "$TARGETARCH" = "arm64" ] && echo "arm64" || echo "x64") \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["./Circles.Pathfinder.Host"]
