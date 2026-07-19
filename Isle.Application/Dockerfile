FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Isle.Application/Isle.Application.csproj", "Isle.Application/"]
COPY ["IsleBridge.Sdk/IsleBridge.Sdk/IsleBridge.Sdk.csproj", "IsleBridge.Sdk/IsleBridge.Sdk/"]
RUN dotnet restore "Isle.Application/Isle.Application.csproj"
COPY . .
WORKDIR "/src/Isle.Application"
RUN dotnet build "./Isle.Application.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Isle.Application.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Isle.Application.dll"]
