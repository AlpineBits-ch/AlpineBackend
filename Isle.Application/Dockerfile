FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Isle.Api/Isle.Api.csproj", "Isle.Api/"]
COPY ["IsleBridge.Sdk/IsleBridge.Sdk/IsleBridge.Sdk.csproj", "IsleBridge.Sdk/IsleBridge.Sdk/"]
RUN dotnet restore "Isle.Api/Isle.Api.csproj"
COPY . .
WORKDIR "/src/Isle.Api"
RUN dotnet build "./Isle.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Isle.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Isle.Api.dll"]
