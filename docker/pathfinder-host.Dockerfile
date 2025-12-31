FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Detect target architecture (amd64/arm64)
ARG TARGETARCH
RUN echo "Building for architecture: ${TARGETARCH}"

# Copy Index, Common, and Pathfinder sources (Pathfinder depends on Circles.Common)
COPY ./src/Index ./Index
COPY ./src/Common ./Common
COPY ./src/Pathfinder ./Pathfinder

# Restore Pathfinder host and its dependencies
RUN dotnet restore ./Pathfinder/Circles.Pathfinder.Host/Circles.Pathfinder.Host.csproj

# Build and publish the project
WORKDIR /src/Pathfinder/Circles.Pathfinder.Host
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["./Circles.Pathfinder.Host"]