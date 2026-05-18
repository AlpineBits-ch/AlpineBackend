FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Install Rust

COPY ["Messaging.Application/Messaging.Application.csproj", "Messaging.Application/"]
RUN dotnet restore "Messaging.Application/Messaging.Application.csproj" -a $TARGETARCH

COPY . .



WORKDIR "/src/Messaging.Application"

RUN dotnet run -c $BUILD_CONFIGURATION -- codegen write

RUN dotnet build "./Messaging.Application.csproj" -c $BUILD_CONFIGURATION -o /app/build -a $TARGETARCH

FROM build AS publish
ARG TARGETARCH
RUN dotnet publish "./Messaging.Application.csproj" -c $BUILD_CONFIGURATION -o /app/publish -a $TARGETARCH /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app


USER root
RUN apt-get update && \
    apt-get install -y ffmpeg libfontconfig1 libfreetype6 && \
    rm -rf /var/lib/apt/lists/*

USER $APP_UID

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Messaging.Application.dll"]