FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# use dotnet related caching
COPY ./src/Circles.Rpc.Host/Circles.Rpc.Host.csproj ./Circles.Rpc.Host/
RUN dotnet restore ./Circles.Rpc.Host/Circles.Rpc.Host.csproj

# Copy all source code
COPY ./src/Circles.Rpc.Host .
WORKDIR /src/Circles.Rpc.Host
RUN dotnet publish \
    -c Release \
    -o /app/publish \
    --no-restore
    
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["./Circles.Rpc.Host"]
