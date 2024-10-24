FROM mcr.microsoft.com/dotnet/sdk:latest AS build
WORKDIR /src

COPY . .

RUN dotnet restore
RUN dotnet publish -c Debug -o /circles-nethermind-plugin

FROM nethermind/nethermind:1.28.0 AS base

# dotnet libs
COPY --from=build /circles-nethermind-plugin/Circles.Index.deps.json /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Common.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Common.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV1.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV1.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.NameRegistry.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.NameRegistry.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.StandardTreasury.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.StandardTreasury.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesViews.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesViews.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Postgres.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Postgres.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Rpc.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Rpc.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Query.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Query.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Utils.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Utils.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Pathfinder.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Pathfinder.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Nethermind.Int256.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Npgsql.dll /nethermind/plugins