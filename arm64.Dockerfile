FROM mcr.microsoft.com/dotnet/sdk:latest AS build
WORKDIR /src

COPY . .

RUN dotnet restore
RUN dotnet publish -c Release -o /circles-nethermind-plugin

FROM nethermind/nethermind:1.25.4 AS base

# native libs
COPY --from=build /circles-nethermind-plugin/runtimes/linux-arm64/native/libe_sqlite3.so /usr/lib/

# dotnet libs
COPY --from=build /circles-nethermind-plugin/Circles.Index.deps.json /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Microsoft.Data.Sqlite.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Nethermind.Int256.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/SQLitePCLRaw.batteries_v2.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/SQLitePCLRaw.core.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/SQLitePCLRaw.provider.e_sqlite3.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Npgsql.dll /nethermind/plugins
