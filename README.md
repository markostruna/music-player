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

Open `http://localhost:4200`. Sign in with the bootstrap administrator, then open **Admin** to add a music folder; its initial scan starts automatically. Scans read existing audio tags and use a `Folder.jpg` or another recognized image in an album folder for artwork, without calling metadata providers. The folder path is a path on the server, not on the listening device. Use the Admin page to create Guest accounts. Guests can play music and choose their own theme; only administrators can change the library or accounts.

The development proxy sends `/music-api` requests to the local API at `http://127.0.0.1:5080` and removes the prefix. Do not put a public server address or credentials in the checked-in proxy configuration.

## Tests And Build

```powershell
npm run build
npm run test -- --watch=false
dotnet test backend/MusicPlayer.Api.Tests/MusicPlayer.Api.Tests.csproj
dotnet build backend/MusicPlayer.Api/MusicPlayer.Api.csproj
```

The Angular production files are emitted to `dist/music-player/browser`.

## XAMPP Hosting

Build the Angular application and copy the **contents** of `dist/music-player/browser` to a directory such as `C:/xampp/htdocs/afterhours`. Configure Apache with HTTPS and enable `mod_proxy`, `mod_proxy_http`, `mod_headers`, `mod_dir`, `mod_filter`, `mod_deflate`, and `mod_ssl`. `Configure-Api.ps1` enables the proxy, header, and compression modules and restarts Apache; run it again with the same `-InstallPath` if the API was already configured before compression was added. Add the following directives to the HTTPS virtual host, adjusting the static directory to match your installation:

```apache
DocumentRoot "C:/xampp/htdocs/afterhours"
<Directory "C:/xampp/htdocs/afterhours">
	Options -Indexes +FollowSymLinks
	AllowOverride All
	Require all granted
	FallbackResource /index.html
</Directory>

ProxyRequests Off
ProxyPreserveHost On
ProxyPass        /music-api/ http://127.0.0.1:5080/ retry=0
ProxyPassReverse /music-api/ http://127.0.0.1:5080/
RequestHeader set X-Forwarded-Proto "https"
```

The Angular build includes an `.htaccess` file that gives fingerprinted JavaScript and CSS bundles a one-year immutable cache and keeps `index.html` revalidated. `AllowOverride All` lets Apache apply these per-directory rules. If your server doesn't allow `.htaccess`, put the same `FilesMatch` and `Files` header rules directly in the HTTPS virtual host instead.

The `.htaccess` file also enables DEFLATE compression for HTML, CSS, JavaScript, JSON, XML, and SVG responses. If your server doesn't allow `.htaccess`, add `AddOutputFilterByType DEFLATE text/html text/plain text/css text/xml application/javascript application/json application/manifest+json image/svg+xml` to the HTTPS virtual host.

Use a trusted TLS certificate before exposing the site to the internet. Run the API as a Windows service or a Task Scheduler task under a dedicated, non-interactive account that can read the configured music folders and write the database directory. Keep port `5080` private; only Apache should accept public connections. In Production, authentication and CSRF cookies are always marked Secure, so sign-in requires HTTPS. Development adapts cookie security to the local request to support `http://localhost:4200`. Back up the SQLite database and music files separately.

The Angular client uses the public `/music-api` prefix, which Apache and the development proxy strip before forwarding to the API's existing `/api` routes. This avoids conflicting with other applications that already use `/api`. No public API port or client-side music path is needed.

The API compresses supported responses (including JSON) with Brotli or gzip when the client advertises support. Compression is enabled for HTTPS because the API is behind Apache; forwarded headers are processed before the compression middleware. Avoid combining attacker-controlled reflected content and secrets in the same compressed response.

Album covers and artist images are delivered as WebP. Existing PNG, JPEG, and GIF artwork is converted when first requested and cached by source content under `%LOCALAPPDATA%\Afterhours\webp-cache`; originals in music folders are not modified. Images uploaded by an administrator keep their original under `%LOCALAPPDATA%\Afterhours\metadata` and also receive a `.webp` sibling. Deploy and restart the API for this behavior to take effect.

## Artist And Album Information

Artist and album information can be refreshed from their detail pages using **Refresh information**. The dialog lets you choose a configured provider, select a candidate, and preview the proposed details; metadata is applied only after you click **Save**. **Refresh metadata** on the Admin page refreshes all distinct artists and albums in a configured folder after it has been scanned. Album lookups use the default identification provider selected in **Settings**; MusicBrainz-linked descriptions and artwork remain available as enrichment. Bulk artist lookups use MusicBrainz and its linked Wikipedia/Wikidata relationships, with Fanart.tv for artist pictures. Scan and metadata-refresh operations run in the background; their status remains visible while browsing other pages and is restored after a browser refresh as long as the API process is still running. Administrators can configure shared Discogs, Spotify, and Fanart.tv credentials from **Settings > Provider integrations**; provider keys are encrypted in SQLite and are never returned by the settings API. Existing server configuration values (including `FanartTv:ApiKey`, `FanartTv:ClientKey`, `Discogs:Token`, `Spotify:ClientId`, and `Spotify:ClientSecret`) remain supported and are used unless overridden in the provider settings page. Data Protection keys are stored under `%LOCALAPPDATA%\Afterhours\keys`; keep that directory private and back it up with the database so encrypted credentials remain available after restoring the service. MusicBrainz requests are rate-limited to one per second, so folder-wide metadata refreshes can take time for large libraries. Retrieved descriptions and image locations are stored in SQLite. MusicBrainz album art is saved beside an album track as `Cover-MusicBrainz.<ext>`; artist art is saved in the matching artist folder as `ArtistCover-MusicBrainz.<ext>`, or beside a matching artist track as `ArtistCover-<artist>-MusicBrainz.<ext>` when no artist folder exists. Images uploaded by an administrator remain under `%LOCALAPPDATA%\Afterhours\metadata`. When no artist image is available, the existing album-art fallback remains visible and the library labels the artist photo as missing. Administrators can edit descriptions and replace album or artist pictures on their detail pages.
