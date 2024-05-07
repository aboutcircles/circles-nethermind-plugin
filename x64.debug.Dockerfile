FROM mcr.microsoft.com/dotnet/sdk:latest AS build
WORKDIR /src

COPY . .

RUN dotnet restore
RUN dotnet publish -c Debug -o /circles-nethermind-plugin

FROM nethermind/nethermind:1.25.4 AS base

# dotnet libs
COPY --from=build /circles-nethermind-plugin/Circles.Index.deps.json /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Common.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Common.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.V1.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.V1.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.V2.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.V2.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Postgres.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Postgres.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Nethermind.Int256.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Npgsql.dll /nethermind/plugins