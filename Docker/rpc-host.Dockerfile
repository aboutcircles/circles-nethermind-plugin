FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/Rpc/Circles.Index.Rpc/Circles.Index.Rpc.csproj ./Circles.Index.Rpc/
RUN dotnet restore
RUN dotnet publish -c Debug -o /circles-nethermind-plugin

COPY ./src/Rpc .

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

COPY --from=build /circles-nethermind-plugin/Circles.Index.Rpc.dll /nethermind/plugins

ENTRYPOINT ["dotnet", "Circles.Rpc.Host.dll"]
