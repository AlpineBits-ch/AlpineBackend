FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
ARG IDENTITY_KEY_PASSWORD
ENV IDENTITY_KEY_PASSWORD=$IDENTITY_KEY_PASSWORD
# Copy all project files
COPY **/*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p ${file%.*}/ && mv $file ${file%.*}/; done

RUN dotnet restore "Identity.Application/Identity.Application.csproj"

COPY . .
WORKDIR "/src/Identity.Application"
RUN dotnet run -c $BUILD_CONFIGURATION -- codegen write

RUN dotnet build "./Identity.Application.csproj" -c $BUILD_CONFIGURATION -o /app/build
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Identity.Application.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# The 'base' and 'final' stages will automatically pull the correct 
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
USER $APP_UID
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Identity.Application.dll"]