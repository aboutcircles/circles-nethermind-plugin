FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# use dotnet related caching
COPY src/Pathfinder/Circles.Pathfinder/Circles.Pathfinder.csproj ./Pathfinder/Circles.Pathfinder/
RUN dotnet restore ./Pathfinder/Circles.Pathfinder/Circles.Pathfinder.csproj

# Copy all source code
COPY ./src/Pathfinder ./Pathfinder
# TODO: remove once index is published separately
COPY ./src/Index ./Index
RUN dotnet publish ./Pathfinder/Circles.Pathfinder/Circles.Pathfinder.csproj -c Debug -o /circles-nethermind-plugin

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
# COPY --from=build /circles-nethermind-plugin/Circles.Pathfinder.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin .
ENTRYPOINT ["dotnet", "Circles.Pathfinder.Host.dll"]
