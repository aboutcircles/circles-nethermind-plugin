FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/Pathfinder/Circles.Pathfinder/Circles.Pathfinder.csproj ./Circles.Pathfinder/
RUN dotnet restore
RUN dotnet publish -c Debug -o /circles-nethermind-plugin

COPY ./src/Pathfinder .
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /circles-nethermind-plugin/Circles.Pathfinder.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin .
ENTRYPOINT ["dotnet", "Circles.Pathfinder.Host.dll"]
