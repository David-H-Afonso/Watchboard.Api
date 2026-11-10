# Watchboard API

API local para seguir versiones de software y precios de juegos. Desarrollada con **ASP.NET Core 10, Entity Framework Core y SQLite**.

[English](README.md) · [Frontend](https://github.com/David-H-Afonso/Watchboard.Front) · [Cambios](CHANGELOG.md)

## Funciones

- Comparación de versiones instaladas y publicaciones de GitHub, npm y fuentes oficiales compatibles.
- Ubicaciones reutilizables, cuentas privadas y widget configurable por usuario.
- Asociación Steam ↔ AllKeyShop con metas e históricos independientes.
- Prioridad para claves o regalos de Steam y alternativa de plataforma de PC indicada.
- Totales publicados con comisiones, mínimos observados y sus fechas.
- Importación y exportación de configuración.

## Inicio

Requiere **.NET 10 SDK**.

```sh
dotnet restore Watchboard.Api.sln --locked-mode
dotnet build Watchboard.Api.sln --no-restore
dotnet run --project Watchboard.Api.csproj --no-launch-profile
```

API: **http://127.0.0.1:5197**. Estado: `GET /api/health`.

Ejecuta el frontend por separado en **5333**. El primer acceso elige la contraseña de `admin`, sin contraseña predeterminada. El administrador crea las demás cuentas.

## Configuración y datos

Valores en `appsettings.json`, reemplazables mediante variables de entorno:

| Variable | Predeterminado |
| --- | --- |
| `Watchboard__DatabasePath` | `storage/watchboard.db` |
| `Watchboard__EnablePolling` | `true` |
| `Watchboard__PriceIntervalMinutes` | `1440` |
| `Watchboard__VersionIntervalMinutes` | `360` |
| `Watchboard__AllowRemoteAccess` | `false` |
| `Watchboard__AllowedOrigins__0` | Sin reemplazo; solo orígenes locales |
| `Watchboard__SecureCookies` | `false` (HTTPS sigue usando cookies Secure) |

Las consultas automáticas solo funcionan con la API encendida; también existe actualización manual. La detección consulta el equipo de la API (el contenedor si se usa Docker), no equipos remotos ni el anfitrión de Docker.

## Docker

GitHub Actions publica **`ghcr.io/david-h-afonso/watchboard-api`** para **Linux amd64** con el `GITHUB_TOKEN` integrado. Los commits de la rama predeterminada actualizan `latest`; cada compilación lleva también `sha-…` completo. Una etiqueta `v1.0.0` publica además `v1.0.0`, `1.0.0` y `1`.

La publicación ARM64 queda pendiente de verificar en hardware nativo; la ejecución de .NET bajo QEMU no fue fiable.

Usa el [Compose del frontend](https://github.com/David-H-Afonso/Watchboard.Front#docker) para ejecutar las dos imágenes. Mantiene **8080 interno**, redirige `/api` desde la web y conserva SQLite y claves de sesión en un volumen. No publiques otro puerto para la API.

Compilación local: `docker build -t watchboard-api .`. La imagen usa un usuario sin privilegios; Compose habilita expresamente las conexiones entre contenedores y los orígenes exactos del navegador. Fuera de esa configuración se conserva el acceso local original.

## Seguridad

La ejecución nativa es **solo loopback** y Compose también enlaza la web a loopback por defecto. El acceso remoto es una opción explícita para una LAN de confianza, no una garantía de seguridad para Internet. Requiere `AllowedOrigins` exactos y `AllowedHosts` correspondientes, sin comodines. Las cookies son protegidas, HttpOnly y SameSite=Strict; los datos privados están separados por usuario. Con terminación HTTPS, configura `Watchboard__SecureCookies=true`. No se confía en cabeceras reenviadas del cliente.

Las migraciones se aplican al arrancar. Antes de actualizar o restaurar, detén la API y respalda **todo el directorio de datos**, incluidas claves y archivos SQLite. No mezcles `.db`, WAL y SHM de distintas copias.

Protege el acceso al volumen: las claves de sesión de Linux se guardan allí sin DPAPI de Windows. No supongas que las claves de sesión protegidas en Windows son portables a Linux.

Los precios externos pueden ser estimados o no estar disponibles. El historial contiene lecturas guardadas, no dos años completos importados. Se excluyen cuentas y tarjetas monedero.

## Licencia

[MIT](LICENSE) © 2026 David-H-Afonso.
