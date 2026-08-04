# Use the platform of the host (x86) for the SDK to avoid slow emulation
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy and restore using the target architecture flag
# This tells the x86 SDK to pull ARM64 libraries when needed

COPY **/*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p ${file%.*}/ && mv $file ${file%.*}/; done


RUN dotnet restore "Unfurl.Application/Unfurl.Application.csproj" -a $TARGETARCH

COPY . .
WORKDIR "/src/Unfurl.Application"

RUN dotnet run -c $BUILD_CONFIGURATION -- codegen write

# Build for the specific architecture
RUN dotnet build "./Unfurl.Application.csproj" -c $BUILD_CONFIGURATION -o /app/build -a $TARGETARCH


FROM build AS publish
ARG TARGETARCH
RUN dotnet publish "./Unfurl.Application.csproj" -c $BUILD_CONFIGURATION -o /app/publish -a $TARGETARCH /p:UseAppHost=false

# The final runtime image matches the target architecture automatically
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
USER $APP_UID
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Unfurl.Application.dll"]
