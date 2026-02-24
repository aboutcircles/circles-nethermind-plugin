FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .

RUN dotnet restore
RUN dotnet publish src/Tools/Circles.TrustMissingAvatars/Circles.TrustMissingAvatars.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Circles.TrustMissingAvatars.dll"]
