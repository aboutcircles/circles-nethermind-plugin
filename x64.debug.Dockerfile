FROM mcr.microsoft.com/dotnet/sdk:latest AS build
WORKDIR /src

COPY . .

RUN dotnet restore
RUN dotnet publish -c Debug -o /circles-nethermind-plugin

FROM jaensen/nethermind-debug AS base

# dotnet libs
COPY --from=build /circles-nethermind-plugin/Circles.Index.deps.json ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.Common.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.Common.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV1.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV1.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV1.NameRegistry.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV1.NameRegistry.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.NameRegistry.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.NameRegistry.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.StandardTreasury.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.StandardTreasury.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.LBP.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.LBP.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.CMGroupDeployer.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.CMGroupDeployer.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.BaseGroupDeployer.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.BaseGroupDeployer.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.AffiliateGroupRegistry.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.AffiliateGroupRegistry.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.InvitationEscrow.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.InvitationEscrow.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.TokenOffers.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesV2.TokenOffers.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesViews.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.CirclesViews.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.Safe.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.Safe.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.Postgres.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.Postgres.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.Rpc.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.Rpc.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.Query.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.Query.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.Profiles.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Index.Profiles.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Dapper.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Pathfinder.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Circles.Pathfinder.pdb ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Nethermind.Int256.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
COPY --from=build /circles-nethermind-plugin/Npgsql.dll ./artifacts/bin/Nethermind.Runner/debug/plugins/
