FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy .csproj files first for better caching
COPY src/Index/Circles.Index/Circles.Index.csproj ./Index/Circles.Index/
COPY src/Index/Circles.Index.CirclesV1/Circles.Index.CirclesV1.csproj ./Index/Circles.Index.CirclesV1/
COPY src/Index/Circles.Index.CirclesV1.NameRegistry/Circles.Index.CirclesV1.NameRegistry.csproj ./Index/Circles.Index.CirclesV1.NameRegistry/
COPY src/Index/Circles.Index.CirclesV2/Circles.Index.CirclesV2.csproj ./Index/Circles.Index.CirclesV2/
COPY src/Index/Circles.Index.CirclesV2.AffiliateGroupRegistry/Circles.Index.CirclesV2.AffiliateGroupRegistry.csproj ./Index/Circles.Index.CirclesV2.AffiliateGroupRegistry/
COPY src/Index/Circles.Index.CirclesV2.BaseGroupDeployer/Circles.Index.CirclesV2.BaseGroupDeployer.csproj ./Index/Circles.Index.CirclesV2.BaseGroupDeployer/
COPY src/Index/Circles.Index.CirclesV2.CMGroupDeployer/Circles.Index.CirclesV2.CMGroupDeployer.csproj ./Index/Circles.Index.CirclesV2.CMGroupDeployer/
COPY src/Index/Circles.Index.CirclesV2.Erc20Lift/Circles.Index.CirclesV2.Erc20Lift.csproj ./Index/Circles.Index.CirclesV2.Erc20Lift/
COPY src/Index/Circles.Index.CirclesV2.InvitationEscrow/Circles.Index.CirclesV2.InvitationEscrow.csproj ./Index/Circles.Index.CirclesV2.InvitationEscrow/
COPY src/Index/Circles.Index.CirclesV2.LBP/Circles.Index.CirclesV2.LBP.csproj ./Index/Circles.Index.CirclesV2.LBP/
COPY src/Index/Circles.Index.CirclesV2.NameRegistry/Circles.Index.CirclesV2.NameRegistry.csproj ./Index/Circles.Index.CirclesV2.NameRegistry/
COPY src/Index/Circles.Index.CirclesV2.OIC/Circles.Index.CirclesV2.OIC.csproj ./Index/Circles.Index.CirclesV2.OIC/
COPY src/Index/Circles.Index.CirclesV2.StandardTreasury/Circles.Index.CirclesV2.StandardTreasury.csproj ./Index/Circles.Index.CirclesV2.StandardTreasury/
COPY src/Index/Circles.Index.CirclesV2.TokenOffers/Circles.Index.CirclesV2.TokenOffers.csproj ./Index/Circles.Index.CirclesV2.TokenOffers/
COPY src/Index/Circles.Index.CirclesViews/Circles.Index.CirclesViews.csproj ./Index/Circles.Index.CirclesViews/
COPY src/Index/Circles.Index.Common/Circles.Index.Common.csproj ./Index/Circles.Index.Common/
COPY src/Index/Circles.Index.Postgres/Circles.Index.Postgres.csproj ./Index/Circles.Index.Postgres/
COPY src/Index/Circles.Index.Profiles/Circles.Index.Profiles.csproj ./Index/Circles.Index.Profiles/
COPY src/Index/Circles.Index.Safe/Circles.Index.Safe.csproj ./Index/Circles.Index.Safe/

# TODO: remove once rpc / pathfinder have their own published module
COPY src/Rpc/Circles.Rpc/Circles.Rpc.csproj ./Rpc/Circles.Rpc/
COPY src/Pathfinder/Circles.Pathfinder/Circles.Pathfinder.csproj ./Pathfinder/Circles.Pathfinder/

# Restore dependencies
RUN dotnet restore ./Index/Circles.Index/Circles.Index.csproj

# Copy all source code
# TODO: remove once rpc / pathfinder have their own published module
COPY ./src/Index ./Index
COPY ./src/Rpc/Circles.Rpc ./Rpc/Circles.Rpc
COPY ./src/Pathfinder/Circles.Pathfinder ./Pathfinder/Circles.Pathfinder

# Build and publish
RUN dotnet publish ./Index/Circles.Index/Circles.Index.csproj -c Release -o /circles-nethermind-plugin

FROM nethermind/nethermind:1.35.2 AS base

WORKDIR /nethermind/plugins

# dotnet libs
COPY --from=build /circles-nethermind-plugin/ .

WORKDIR /nethermind