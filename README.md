# MusicPlayer

This project was generated using [Angular CLI](https://github.com/angular/angular-cli) version 22.2.0.

## Development server

To start a local development server, run:

```bash
ng serve
```

Once the server is running, open your browser and navigate to `http://localhost:4200/`. The application will automatically reload whenever you modify any of the source files.

## Code scaffolding

Angular CLI includes powerful code scaffolding tools. To generate a new component, run:

```bash
ng generate component component-name
```

For a complete list of available schematics (such as `components`, `directives`, or `pipes`), run:

```bash
ng generate --help
```

## Building

To build the project run:

```bash
ng build
```

This will compile your project and store the build artifacts in the `dist/` directory. By default, the production build optimizes your application for performance and speed.

## Running unit tests

To execute unit tests with the [Vitest](https://vitest.dev/) test runner, use the following command:

```bash
ng test
```

## Running end-to-end tests

For end-to-end (e2e) testing, run:

# Afterhours Music Player

Afterhours is a self-hosted music library. The Angular client plays audio from an ASP.NET Core API, which scans only administrator-configured folders on the server. The API supports MP3, FLAC, and WAV files. There is no public registration: the first administrator is bootstrapped once, and administrators create subsequent accounts.

## Requirements

- Windows with .NET 10 SDK and Node.js/npm
- XAMPP/Apache for public HTTPS hosting
- Music folders readable by the Windows account running the API

## First Run

Start the API from the repository root. The API listens only on `127.0.0.1:5080` and creates its SQLite database under `%LOCALAPPDATA%\Afterhours` by default.

Before the first API start, set `MUSICPLAYER_BOOTSTRAP_EMAIL` and `MUSICPLAYER_BOOTSTRAP_PASSWORD` in the environment of the account that will run the API. Use a unique password of at least 12 characters; do not commit credentials or place them in shared scripts. The bootstrap account is created only when the database has no users. Remove the password variable after the first successful start. To choose a different database file, set `MUSICPLAYER_DATABASE_PATH` before starting the API.

In a PowerShell window:

```powershell
dotnet run --project backend/MusicPlayer.Api
```

In a second PowerShell window, start Angular:

```powershell
npm install
npm start
```

Open `http://localhost:4200`. Sign in with the bootstrap administrator, then open **Admin** to add a music folder and scan it. The folder path is a path on the server, not on the listening device. Use the Admin page to create Guest accounts. Guests can play music and choose their own theme; only administrators can change the library or accounts.

## Tests And Build

```powershell
npm run build
npm run test -- --watch=false
dotnet test backend/MusicPlayer.Api.Tests/MusicPlayer.Api.Tests.csproj
dotnet build backend/MusicPlayer.Api/MusicPlayer.Api.csproj
```

The Angular production files are emitted to `dist/music-player/browser`.

## XAMPP Hosting

Build the Angular application and copy the **contents** of `dist/music-player/browser` to a directory such as `C:/xampp/htdocs/afterhours`. Configure Apache with HTTPS and enable `mod_proxy`, `mod_proxy_http`, `mod_headers`, `mod_dir`, and `mod_ssl`. Add the following directives to the HTTPS virtual host, adjusting the static directory to match your installation:

```apache
DocumentRoot "C:/xampp/htdocs/afterhours"
<Directory "C:/xampp/htdocs/afterhours">
	Options -Indexes +FollowSymLinks
	Require all granted
	FallbackResource /index.html
</Directory>

ProxyRequests Off
ProxyPreserveHost On
ProxyPass        /music-api/ http://127.0.0.1:5080/ retry=0
ProxyPassReverse /music-api/ http://127.0.0.1:5080/
RequestHeader set X-Forwarded-Proto "https"
```

Use a trusted TLS certificate before exposing the site to the internet. Run the API as a Windows service or a Task Scheduler task under a dedicated, non-interactive account that can read the configured music folders and write the database directory. Keep port `5080` private; only Apache should accept public connections. In Production, authentication and CSRF cookies are always marked Secure, so sign-in requires HTTPS. Development adapts cookie security to the local request to support `http://localhost:4200`. Back up the SQLite database and music files separately.

The Angular client uses the public `/music-api` prefix, which Apache and the development proxy strip before forwarding to the API's existing `/api` routes. This avoids conflicting with other applications that already use `/api`. No public API port or client-side music path is needed.

## Artist And Album Information

Artist and album information can be refreshed from their detail pages using **Refresh information**, or for all distinct artists and albums in a configured folder using **Refresh metadata** on the Admin page after the folder has been scanned. Folder scans only read the music files and do not call metadata providers. Scan and metadata-refresh operations run in the background; their status remains visible while browsing other pages and is restored after a browser refresh as long as the API process is still running. Metadata lookups use MusicBrainz and its linked Wikipedia/Wikidata relationships for descriptions, Fanart.tv for artist pictures, and Cover Art Archive for album artwork. Configure a Fanart.tv developer key in `%LOCALAPPDATA%\Afterhours\appsettings.json` under `FanartTv:ApiKey`; if it is absent, the personal key in `FanartTv:ClientKey` is used as the API key. This file is local to the API account and must not be committed. The API also accepts the corresponding `FanartTv__ApiKey` and `FanartTv__ClientKey` environment variables. MusicBrainz requests are rate-limited to one per second, so folder-wide metadata refreshes can take time for large libraries. Retrieved descriptions and image locations are stored in SQLite. MusicBrainz album art is saved beside an album track as `Cover-MusicBrainz.<ext>`; artist art is saved in the matching artist folder as `ArtistCover-MusicBrainz.<ext>`, or beside a matching artist track as `ArtistCover-<artist>-MusicBrainz.<ext>` when no artist folder exists. Images uploaded by an administrator remain under `%LOCALAPPDATA%\Afterhours\metadata`. When no artist image is available, the existing album-art fallback remains visible and the library labels the artist photo as missing. Administrators can edit descriptions and replace album or artist pictures on their detail pages.
