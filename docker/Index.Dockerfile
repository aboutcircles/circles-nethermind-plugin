FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy shared build props (defines TFM)
COPY Directory.Build.props ./

# Copy .csproj files first for better caching (preserving original directory structure)
COPY src/Index/Circles.Index/Circles.Index.csproj ./Index/Circles.Index/
COPY src/Index/Circles.Index.CirclesV1/Circles.Index.CirclesV1.csproj ./Index/Circles.Index.CirclesV1/
COPY src/Index/Circles.Index.CirclesV1.NameRegistry/Circles.Index.CirclesV1.NameRegistry.csproj ./Index/Circles.Index.CirclesV1.NameRegistry/
COPY src/Index/Circles.Index.CirclesV2/Circles.Index.CirclesV2.csproj ./Index/Circles.Index.CirclesV2/
COPY src/Index/Circles.Index.CirclesV2.AffiliateGroupRegistry/Circles.Index.CirclesV2.AffiliateGroupRegistry.csproj ./Index/Circles.Index.CirclesV2.AffiliateGroupRegistry/
COPY src/Index/Circles.Index.CirclesV2.BaseGroupDeployer/Circles.Index.CirclesV2.BaseGroupDeployer.csproj ./Index/Circles.Index.CirclesV2.BaseGroupDeployer/
COPY src/Index/Circles.Index.CirclesV2.CMGroupDeployer/Circles.Index.CirclesV2.CMGroupDeployer.csproj ./Index/Circles.Index.CirclesV2.CMGroupDeployer/
COPY src/Index/Circles.Index.CirclesV2.Erc20Lift/Circles.Index.CirclesV2.Erc20Lift.csproj ./Index/Circles.Index.CirclesV2.Erc20Lift/
COPY src/Index/Circles.Index.CirclesV2.InvitationEscrow/Circles.Index.CirclesV2.InvitationEscrow.csproj ./Index/Circles.Index.CirclesV2.InvitationEscrow/
COPY src/Index/Circles.Index.CirclesV2.InvitationsAtScale/Circles.Index.CirclesV2.InvitationsAtScale.csproj ./Index/Circles.Index.CirclesV2.InvitationsAtScale/
COPY src/Index/Circles.Index.CirclesV2.LBP/Circles.Index.CirclesV2.LBP.csproj ./Index/Circles.Index.CirclesV2.LBP/
COPY src/Index/Circles.Index.CirclesV2.NameRegistry/Circles.Index.CirclesV2.NameRegistry.csproj ./Index/Circles.Index.CirclesV2.NameRegistry/
COPY src/Index/Circles.Index.CirclesV2.OIC/Circles.Index.CirclesV2.OIC.csproj ./Index/Circles.Index.CirclesV2.OIC/
COPY src/Index/Circles.Index.CirclesV2.PaymentGateway/Circles.Index.CirclesV2.PaymentGateway.csproj ./Index/Circles.Index.CirclesV2.PaymentGateway/
COPY src/Index/Circles.Index.CirclesV2.ScoreGroup/Circles.Index.CirclesV2.ScoreGroup.csproj ./Index/Circles.Index.CirclesV2.ScoreGroup/
COPY src/Index/Circles.Index.CirclesV2.StandardTreasury/Circles.Index.CirclesV2.StandardTreasury.csproj ./Index/Circles.Index.CirclesV2.StandardTreasury/
COPY src/Index/Circles.Index.CirclesV2.TokenOffers/Circles.Index.CirclesV2.TokenOffers.csproj ./Index/Circles.Index.CirclesV2.TokenOffers/
COPY src/Index/Circles.Index.CirclesViews/Circles.Index.CirclesViews.csproj ./Index/Circles.Index.CirclesViews/
COPY src/Index/Circles.Index.DatabaseSchemaProvider/Circles.Index.DatabaseSchemaProvider.csproj ./Index/Circles.Index.DatabaseSchemaProvider/
COPY src/Index/Circles.Index.Postgres/Circles.Index.Postgres.csproj ./Index/Circles.Index.Postgres/
COPY src/Index/Circles.Index.Profiles/Circles.Index.Profiles.csproj ./Index/Circles.Index.Profiles/
COPY src/Index/Circles.Index.Query/Circles.Index.Query.csproj ./Index/Circles.Index.Query/
COPY src/Index/Circles.Index.Safe/Circles.Index.Safe.csproj ./Index/Circles.Index.Safe/
COPY src/Common/Circles.Common/Circles.Common.csproj ./Common/Circles.Common/

# Restore dependencies
RUN dotnet restore ./Index/Circles.Index/Circles.Index.csproj

# Copy all source code
COPY ./src/Index ./Index
COPY ./src/Common ./Common
WORKDIR /src/Index/Circles.Index

# Build and publish
RUN dotnet publish \
    -c Release \
    -o /circles-nethermind-plugin \
    --no-restore

FROM nethermind/nethermind:1.37.2 AS base

# Install psql for manual DB operations (runs as root, compose sets runtime user)
RUN apt-get update && apt-get install -y postgresql-client && rm -rf /var/lib/apt/lists/*

WORKDIR /nethermind/plugins

# dotnet libs
COPY --from=build /circles-nethermind-plugin/ .
WORKDIR /nethermind
