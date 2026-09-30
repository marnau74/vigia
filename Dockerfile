# Tres imágenes desde un mismo compilado: api, web y worker.
#   docker build --target api    -t vigia-api    .
#   docker build --target web    -t vigia-web    .
#   docker build --target worker -t vigia-worker .
# Se construyen desde la raíz del repositorio (necesitan los ficheros comunes: versiones de paquetes, normas de código…).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS compilar
WORKDIR /origen

# Sin las normas de código (.editorconfig) los analizadores dan otros avisos, y aquí los avisos son errores.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/

RUN dotnet publish src/Vigia.Api    -c Release -o /publicar/api
RUN dotnet publish src/Vigia.Web    -c Release -o /publicar/web
RUN dotnet publish src/Vigia.Worker -c Release -o /publicar/worker

# --- Base de ejecución: sin root y con curl para las comprobaciones de salud de Docker ---------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
HEALTHCHECK --interval=15s --timeout=3s --start-period=40s --retries=3 CMD curl -fsS http://localhost:8080/health || exit 1
WORKDIR /app

FROM base AS api
COPY --from=compilar /publicar/api .
ENTRYPOINT ["dotnet", "Vigia.Api.dll"]

FROM base AS web
# Las claves de cifrado de la cookie de sesión viven en un volumen: el directorio debe ser del usuario sin privilegios
# (un volumen nuevo hereda el dueño del directorio de la imagen; si fuera de root, la web no podría escribir y daría 500).
USER root
RUN mkdir /claves && chown app:app /claves
USER app
COPY --from=compilar /publicar/web .
ENTRYPOINT ["dotnet", "Vigia.Web.dll"]

FROM base AS worker
COPY --from=compilar /publicar/worker .
ENTRYPOINT ["dotnet", "Vigia.Worker.dll"]
