FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props ./
COPY src/SquadHostManager.Core/ src/SquadHostManager.Core/
COPY src/SquadHostManager.Service/ src/SquadHostManager.Service/
RUN dotnet publish src/SquadHostManager.Service -c Release -o /app --no-self-contained
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080 DataDir=/data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SquadHostManager.Service.dll"]
