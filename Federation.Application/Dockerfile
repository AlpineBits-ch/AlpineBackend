FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
WORKDIR /src


COPY **/*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p ${file%.*}/ && mv $file ${file%.*}/; done

RUN dotnet restore "Federation.Application/Federation.Application.csproj" -a $TARGETARCH

COPY . .
WORKDIR "/src/Federation.Application"
RUN dotnet run -c $BUILD_CONFIGURATION -- codegen write

RUN dotnet build "./Federation.Application.csproj" -c $BUILD_CONFIGURATION -o /app/build -a $TARGETARCH


FROM build AS publish
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Federation.Application.csproj" -c $BUILD_CONFIGURATION -o /app/publish -a $TARGETARCH /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Federation.Application.dll"]