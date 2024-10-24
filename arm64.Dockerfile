FROM mcr.microsoft.com/dotnet/sdk:latest AS build
WORKDIR /src

COPY . .

RUN dotnet restore
RUN dotnet publish -c Release -o /circles-nethermind-plugin

FROM nethermind/nethermind:1.28.0 AS base

# dotnet libs
COPY --from=build /circles-nethermind-plugin/Circles.Index.deps.json /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Common.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV1.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.NameRegistry.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.StandardTreasury.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesViews.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Rpc.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Query.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Utils.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Pathfinder.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Nethermind.Int256.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Npgsql.dll /nethermind/plugins
