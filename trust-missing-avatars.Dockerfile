FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY . .

RUN dotnet restore
RUN dotnet publish Circles.Index.TrustMissingAvatars/Circles.Index.TrustMissingAvatars.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Circles.Index.TrustMissingAvatars.dll"]
