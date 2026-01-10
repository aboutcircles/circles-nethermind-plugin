FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy only the backfill project (it's self-contained, no internal dependencies)
COPY src/Index/Circles.Index.Backfill/Circles.Index.Backfill.csproj ./
RUN dotnet restore

# Copy source and build
COPY src/Index/Circles.Index.Backfill/ ./
RUN dotnet publish -c Release -o /app --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "Circles.Index.Backfill.dll"]
