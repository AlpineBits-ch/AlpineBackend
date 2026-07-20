# Start from the base runtime - Docker will automatically pull the 
# correct arch (amd64 or arm64) for this stage during the final build
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# CRITICAL: Run the SDK on the BUILDER'S native platform (x86) to avoid emulation
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy and restore for the specific target architecture
COPY **/*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p ${file%.*}/ && mv $file ${file%.*}/; done


RUN dotnet restore "Isle.Application/Isle.Application.csproj" -a $TARGETARCH

COPY . .
WORKDIR "/src/Isle.Application"

RUN dotnet run -c $BUILD_CONFIGURATION -- codegen write

# Build for the target architecture
RUN dotnet build "./Isle.Application.csproj" -c $BUILD_CONFIGURATION -o /app/build -a $TARGETARCH


FROM build AS publish
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
# Publish specifically for the target architecture
RUN dotnet publish "./Isle.Application.csproj" -c $BUILD_CONFIGURATION -o /app/publish -a $TARGETARCH /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Isle.Application.dll"]