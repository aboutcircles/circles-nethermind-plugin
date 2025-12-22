FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY . .
    
RUN dotnet restore
RUN dotnet publish -c Debug -o /circles-nethermind-plugin

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /circles-nethermind-plugin .
ENTRYPOINT ["dotnet", "Circles.Pathfinder.Host.dll"]
