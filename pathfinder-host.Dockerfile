FROM mcr.microsoft.com/dotnet/sdk:latest AS build
WORKDIR /src

COPY . .
    
RUN dotnet restore
RUN dotnet publish -c Debug -o /circles-nethermind-plugin

FROM mcr.microsoft.com/dotnet/aspnet:latest AS final
WORKDIR /app
COPY --from=build /circles-nethermind-plugin .
ENTRYPOINT ["dotnet", "Circles.Pathfinder.Host.dll"]
