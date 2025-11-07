FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# use dotnet related caching
COPY src/Rpc/Circles.Rpc/Circles.Rpc.csproj ./Rpc/Circles.Rpc/
RUN dotnet restore ./Rpc/Circles.Rpc/Circles.Rpc.csproj

# Copy all source code
COPY ./src/Rpc ./Rpc
# TODO: remove once index and pathfinder are published separately
COPY ./src/Pathfinder ./Pathfinder
COPY ./src/Index ./Index
RUN dotnet publish ./Rpc/Circles.Rpc/Circles.Rpc.csproj -c Debug -o /circles-nethermind-plugin

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
# COPY --from=build /circles-nethermind-plugin/Circles.Rpc.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin .
ENTRYPOINT ["dotnet", "Circles.Rpc.Host.dll"]
