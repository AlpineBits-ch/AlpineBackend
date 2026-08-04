FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Every csproj first, then restore, then the sources - so the restore layer is cached against the
# project files alone and only re-runs when a dependency actually changes.
#
# This used to copy Messaging.Application.csproj on its own. That project has a dozen
# ProjectReferences whose csproj files were therefore absent, so the restore below could not
# resolve them and the real restore happened implicitly during `dotnet run`/`dotnet build` after
# `COPY . .` - i.e. on the layer that is invalidated by any source edit at all. The result was a
# full cold restore of the whole graph on every single build, which is why this image took hours
# while its siblings (all of which already did it this way) took minutes.
COPY **/*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p ${file%.*}/ && mv $file ${file%.*}/; done

RUN dotnet restore "Messaging.Application/Messaging.Application.csproj"

COPY . .

WORKDIR "/src/Messaging.Application"

RUN dotnet run -c $BUILD_CONFIGURATION -- codegen write

RUN dotnet build "./Messaging.Application.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
RUN dotnet publish "./Messaging.Application.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

USER root
RUN apt-get update && \
    apt-get install -y ffmpeg libfontconfig1 libfreetype6 && \
    rm -rf /var/lib/apt/lists/*

USER $APP_UID

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Messaging.Application.dll"]