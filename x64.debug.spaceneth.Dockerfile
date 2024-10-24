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
