# We use the build platform (x86) for the SDK to keep it fast
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
ARG IDENTITY_KEY_PASSWORD
ENV IDENTITY_KEY_PASSWORD=$IDENTITY_KEY_PASSWORD
# Copy all project files
COPY **/*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p ${file%.*}/ && mv $file ${file%.*}/; done

# Restore specifically for the target architecture (-a)
RUN dotnet restore "Identity.Application/Identity.Application.csproj" -a $TARGETARCH

COPY . .
WORKDIR "/src/Identity.Application"
RUN dotnet run -c $BUILD_CONFIGURATION -- codegen write

# Build for the target architecture
RUN dotnet build "./Identity.Application.csproj" -c $BUILD_CONFIGURATION -o /app/build -a $TARGETARCH
FROM build AS publish
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
# Publish for the target architecture
RUN dotnet publish "./Identity.Application.csproj" -c $BUILD_CONFIGURATION -o /app/publish -a $TARGETARCH /p:UseAppHost=false

# The 'base' and 'final' stages will automatically pull the correct 
# architecture (x86 or ARM) based on the Buildx target
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
USER $APP_UID
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Identity.Application.dll"]