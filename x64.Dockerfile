FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy .csproj files first for better caching
COPY Circles.Index/Circles.Index.csproj ./Circles.Index/
COPY Circles.Index.CirclesV1/Circles.Index.CirclesV1.csproj ./Circles.Index.CirclesV1/
COPY Circles.Index.CirclesV1.NameRegistry/Circles.Index.CirclesV1.NameRegistry.csproj ./Circles.Index.CirclesV1.NameRegistry/
COPY Circles.Index.CirclesV2/Circles.Index.CirclesV2.csproj ./Circles.Index.CirclesV2/
COPY Circles.Index.CirclesV2.AffiliateGroupRegistry/Circles.Index.CirclesV2.AffiliateGroupRegistry.csproj ./Circles.Index.CirclesV2.AffiliateGroupRegistry/
COPY Circles.Index.CirclesV2.BaseGroupDeployer/Circles.Index.CirclesV2.BaseGroupDeployer.csproj ./Circles.Index.CirclesV2.BaseGroupDeployer/
COPY Circles.Index.CirclesV2.CMGroupDeployer/Circles.Index.CirclesV2.CMGroupDeployer.csproj ./Circles.Index.CirclesV2.CMGroupDeployer/
COPY Circles.Index.CirclesV2.Erc20Lift/Circles.Index.CirclesV2.Erc20Lift.csproj ./Circles.Index.CirclesV2.Erc20Lift/
COPY Circles.Index.CirclesV2.InvitationEscrow/Circles.Index.CirclesV2.InvitationEscrow.csproj ./Circles.Index.CirclesV2.InvitationEscrow/
COPY Circles.Index.CirclesV2.LBP/Circles.Index.CirclesV2.LBP.csproj ./Circles.Index.CirclesV2.LBP/
COPY Circles.Index.CirclesV2.NameRegistry/Circles.Index.CirclesV2.NameRegistry.csproj ./Circles.Index.CirclesV2.NameRegistry/
COPY Circles.Index.CirclesV2.OIC/Circles.Index.CirclesV2.OIC.csproj ./Circles.Index.CirclesV2.OIC/
COPY Circles.Index.CirclesV2.StandardTreasury/Circles.Index.CirclesV2.StandardTreasury.csproj ./Circles.Index.CirclesV2.StandardTreasury/
COPY Circles.Index.CirclesV2.TokenOffers/Circles.Index.CirclesV2.TokenOffers.csproj ./Circles.Index.CirclesV2.TokenOffers/
COPY Circles.Index.CirclesViews/Circles.Index.CirclesViews.csproj ./Circles.Index.CirclesViews/
COPY Circles.Index.Common/Circles.Index.Common.csproj ./Circles.Index.Common/
COPY Circles.Index.Postgres/Circles.Index.Postgres.csproj ./Circles.Index.Postgres/
COPY Circles.Index.Profiles/Circles.Index.Profiles.csproj ./Circles.Index.Profiles/
COPY Circles.Index.Rpc/Circles.Index.Rpc.csproj ./Circles.Index.Rpc/
COPY Circles.Index.Safe/Circles.Index.Safe.csproj ./Circles.Index.Safe/

# Restore dependencies
RUN dotnet restore ./Circles.Index/Circles.Index.csproj

# Copy all source code
COPY . .

# Build and publish
RUN dotnet publish -c Release -o /circles-nethermind-plugin

FROM nethermind/nethermind:1.35.8 AS base

# dotnet libs
COPY --from=build /circles-nethermind-plugin/Circles.Index.deps.json /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Common.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV1.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV1.NameRegistry.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.NameRegistry.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.StandardTreasury.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.LBP.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.CMGroupDeployer.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.BaseGroupDeployer.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.AffiliateGroupRegistry.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.InvitationEscrow.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.TokenOffers.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.OIC.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesViews.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Safe.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Postgres.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Rpc.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Query.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Index.Profiles.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Circles.Pathfinder.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Nethermind.Int256.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Npgsql.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Dapper.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Microsoft.Extensions.Caching.Abstractions.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Microsoft.Extensions.Caching.Memory.dll /nethermind/plugins

COPY --from=build /circles-nethermind-plugin/Google.OrTools.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/Google.Protobuf.dll /nethermind/plugins
COPY --from=build /circles-nethermind-plugin/runtimes/linux-x64/native/* /nethermind/plugins/
