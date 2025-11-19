FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Detect target architecture
ARG TARGETARCH
RUN echo "Building for architecture: ${TARGETARCH}"

# Copy all necessary project files for dependency resolution
COPY src/Pathfinder/Circles.Pathfinder/Circles.Pathfinder.csproj ./Circles.Pathfinder/
COPY src/Pathfinder/Circles.Pathfinder.Host/Circles.Pathfinder.Host.csproj ./Circles.Pathfinder.Host/
COPY src/Index/Circles.Index/Circles.Index.csproj ./Circles.Index/
COPY src/Index/Circles.Index.Common/Circles.Index.Common.csproj ./Circles.Index.Common/
COPY src/Index/Circles.Index.CirclesV1/Circles.Index.CirclesV1.csproj ./Circles.Index.CirclesV1/
COPY src/Index/Circles.Index.CirclesV1.NameRegistry/Circles.Index.CirclesV1.NameRegistry.csproj ./Circles.Index.CirclesV1.NameRegistry/
COPY src/Index/Circles.Index.CirclesV2/Circles.Index.CirclesV2.csproj ./Circles.Index.CirclesV2/
COPY src/Index/Circles.Index.CirclesV2.AffiliateGroupRegistry/Circles.Index.CirclesV2.AffiliateGroupRegistry.csproj ./Circles.Index.CirclesV2.AffiliateGroupRegistry/
COPY src/Index/Circles.Index.CirclesV2.BaseGroupDeployer/Circles.Index.CirclesV2.BaseGroupDeployer.csproj ./Circles.Index.CirclesV2.BaseGroupDeployer/
COPY src/Index/Circles.Index.CirclesV2.CMGroupDeployer/Circles.Index.CirclesV2.CMGroupDeployer.csproj ./Circles.Index.CirclesV2.CMGroupDeployer/
COPY src/Index/Circles.Index.CirclesV2.Erc20Lift/Circles.Index.CirclesV2.Erc20Lift.csproj ./Circles.Index.CirclesV2.Erc20Lift/
COPY src/Index/Circles.Index.CirclesV2.InvitationEscrow/Circles.Index.CirclesV2.InvitationEscrow.csproj ./Circles.Index.CirclesV2.InvitationEscrow/
COPY src/Index/Circles.Index.CirclesV2.LBP/Circles.Index.CirclesV2.LBP.csproj ./Circles.Index.CirclesV2.LBP/
COPY src/Index/Circles.Index.CirclesV2.NameRegistry/Circles.Index.CirclesV2.NameRegistry.csproj ./Circles.Index.CirclesV2.NameRegistry/
COPY src/Index/Circles.Index.CirclesV2.OIC/Circles.Index.CirclesV2.OIC.csproj ./Circles.Index.CirclesV2.OIC/
COPY src/Index/Circles.Index.CirclesV2.StandardTreasury/Circles.Index.CirclesV2.StandardTreasury.csproj ./Circles.Index.CirclesV2.StandardTreasury/
COPY src/Index/Circles.Index.CirclesV2.TokenOffers/Circles.Index.CirclesV2.TokenOffers.csproj ./Circles.Index.CirclesV2.TokenOffers/
COPY src/Index/Circles.Index.CirclesViews/Circles.Index.CirclesViews.csproj ./Circles.Index.CirclesViews/
COPY src/Index/Circles.Index.Postgres/Circles.Index.Postgres.csproj ./Circles.Index.Postgres/
COPY src/Index/Circles.Index.Profiles/Circles.Index.Profiles.csproj ./Circles.Index.Profiles/
COPY src/Index/Circles.Index.Safe/Circles.Index.Safe.csproj ./Circles.Index.Safe/

# Restore just the target project - this will automatically restore its dependencies
RUN dotnet restore Circles.Pathfinder.Host/Circles.Pathfinder.Host.csproj

# Copy all source code
COPY ./src/ .
WORKDIR /src/Pathfinder/Circles.Pathfinder.Host

# Build and publish (removed --no-restore to allow automatic restore if needed)
RUN dotnet publish \
    -c Release \
    -r linux-$([ "$TARGETARCH" = "arm64" ] && echo "arm64" || echo "x64") \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["./Circles.Pathfinder.Host"]