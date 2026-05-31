FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/NotifyCenter.Api/NotifyCenter.Api.csproj src/NotifyCenter.Api/
RUN dotnet restore src/NotifyCenter.Api/NotifyCenter.Api.csproj

COPY src/NotifyCenter.Api src/NotifyCenter.Api
RUN dotnet publish src/NotifyCenter.Api/NotifyCenter.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 17052
ENV ASPNETCORE_URLS=http://+:17052

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "NotifyCenter.Api.dll"]
