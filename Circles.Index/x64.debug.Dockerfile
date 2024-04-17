FROM mcr.microsoft.com/dotnet/sdk:latest AS build
WORKDIR /src

COPY . .

RUN dotnet restore
RUN dotnet publish -c Debug -o /circles-nethermind-plugin

FROM nethermind/nethermind:1.25.4 AS base

# native libs
COPY --from=build /circles-nethermind-plugin/runtimes/linux-x64/native/libe_sqlite3.so /usr/lib/

# dotnet libs
COPY --from=build /circles-nethermind-plugin/Circles.Index.deps.json /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.pdb /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Microsoft.Data.Sqlite.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Nethermind.Int256.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/SQLitePCLRaw.batteries_v2.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/SQLitePCLRaw.core.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/SQLitePCLRaw.provider.e_sqlite3.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Npgsql.dll /nethermind/plugins

# data files
COPY --from=build /circles-nethermind-plugin/Indexer/Cheatcodes/relevant_blocks.csv /nethermind/plugins/circles_index_known_blocks.csv


# FROM base AS circles-nethermind
# Install required packages
RUN apt-get update && \
    apt-get install -y libfaketime python3 python3-flask && \
    rm -rf /var/lib/apt/lists/*

# Copy configuration files
COPY ./circles-chainspec.json /nethermind/chainspec/circles.json
COPY ./circles-config.cfg /nethermind/configs/circles.cfg

# Set libfaketime to 'real time'
ENV FAKETIME="+0 x1" FAKETIME_NO_CACHE=1

# Copy the Python script for the API
COPY ./time_controller.py /app/time_controller.py

# Expose the API port
EXPOSE 5000

# Start the API and Nethermind
ENTRYPOINT ["sh", "-c", "python3 /app/time_controller.py"]
