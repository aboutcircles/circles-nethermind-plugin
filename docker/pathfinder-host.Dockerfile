FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# use dotnet related caching
COPY src/Pathfinder/Circles.Pathfinder/Circles.Pathfinder.csproj ./Circles.Pathfinder/
COPY src/Pathfinder/Circles.Pathfinder.Host/Circles.Pathfinder.Host.csproj ./Circles.Pathfinder.Host/
RUN dotnet restore ./Circles.Pathfinder.Host/Circles.Pathfinder.Host.csproj

# Copy all source code
COPY ./src/Pathfinder .
WORKDIR /src/Circles.Pathfinder.Host
RUN dotnet publish \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["./Circles.Pathfinder.Host"]
