FROM mcr.microsoft.com/dotnet/sdk:latest AS build
WORKDIR /src

COPY . .
RUN find . -type f -name "._*" -delete && \
    find . -type f -name "*.cs" -exec dos2unix {} \;
    
RUN dotnet restore
RUN dotnet publish -c Release -o /circles-nethermind-plugin

FROM mcr.microsoft.com/dotnet/aspnet:latest AS final
WORKDIR /app
COPY --from=build /circles-nethermind-plugin .
ENTRYPOINT ["dotnet", "Circles.Pathfinder.Host.dll"]
