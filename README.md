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
- FFmpeg on the API server's `PATH` when splitting single-FLAC CUE albums

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

Open `http://localhost:4200`. Sign in with the bootstrap administrator, then open **Admin** to add a music folder; its initial scan starts automatically. Scans read existing audio tags and use a `Folder.jpg` or another recognized image in an album folder for artwork, without calling metadata providers. The folder path is a path on the server, not on the listening device.

Administrators can also register **Unscanned folders** as album inboxes. These folders must not overlap a configured music source. Open **Folders** to browse the inbox, rename directories or audio filenames, check artist/album names against the catalog, and move an album into a scanned source. Imports scan only the imported album folder and then refresh metadata for its artists and albums automatically. When a matching album exists, choose to rename or delete it before importing. If an album has a single FLAC and a CUE file, choose whether to split it into track FLACs; splitting requires FFmpeg on the API server's `PATH`, and you can keep the original FLAC/CUE in the inbox or delete them after a successful import. Unscanned inboxes are visible to administrators only.

Use **Clean database** on the Admin page to remove catalog entries for missing tracks and folders in scanned sources. It never deletes files, configured sources, or metadata. Unavailable or unsafe sources are skipped and reported in the operation result.

The home page shows up to seven **Added recently** albums. Album and artist import times are derived from the earliest catalogued track and do not change on rescans. For entries already in the database when this feature is installed, the previous scan time is used as an approximation because their original import time was not recorded.

**Played recently** shows up to ten available tracks whose latest play reached the end, ordered by completion time, along with their number of recorded playback sessions. **Continue listening** shows up to ten tracks whose latest play has not ended and whose saved position is between the beginning and the end; selecting one resumes from that position. The progress bar is based on the saved position and track duration. Older history predating completion tracking is considered complete only when its saved position reaches the catalogued duration; other completed plays cannot be reliably recovered. Both sections refresh while Home is open and use the signed-in account's synchronized history; the demo does not record history.

Use the Admin page to create Guest accounts. Guests can play music and choose their own theme; only administrators can change the library or accounts.

## Playlists

Playlists belong to the signed-in account. Create and rename them on **Playlists**, add tracks from the library's Tracks view, and reorder or remove tracks on the playlist page. Home shows the first eight personal playlists and four fixed selections. Unavailable tracks are hidden but retained in their playlists until available again; database cleanup can permanently remove deleted catalog entries.

Choose from 20 built-in background pictures on a personal playlist's page. The four fixed playlists use their own artwork and cannot be changed. Background choices are saved with the playlist.

**Random albums** queues up to ten complete albums; **Random tracks** queues up to 100 tracks. **Album radio** replaces its queue with another album at the end of each album, while **Track radio** selects one track at a time until stopped. Selections use the available library, so a smaller library produces a smaller queue. Radio follows synchronized playback between devices; with synchronization disabled it runs in the current browser. The demo keeps playlist edits in memory until reload.

## Playback Synchronization

Playback synchronization is enabled by default. The API saves each account's queue, current track, position, shuffle and repeat settings, and a history entry with the last position for every played track. History is retained in the SQLite database and can be retrieved by the signed-in user from `GET /api/playback/history?page=1&pageSize=50`.
The Home summaries are available at `GET /api/playback/statistics`.

Signed-in browsers share transport controls and the queue. The **Now playing** page shows the current playback device and lets you select another one or choose **Play on this device**. The previous device stops before a requested handoff. If the playback device disconnects, another connected device takes over after its connection lease expires (up to about 15 seconds). Device availability is held in the API process, so synchronization requires a single running API instance.

The **Playback synchronization** setting applies to this account in the current browser, including its tabs. When disabled, the browser plays independently and does not send its playback or history to the server. Re-enabling synchronization replaces its local queue with the saved shared queue. The demo always plays locally.

Playback position is checkpointed approximately every two seconds and when leaving the page; an abrupt termination or connection failure can lose the latest progress. Browsers may block automatic audio after a handoff or reload. If that happens, use **Play** on the selected device to resume.

The development proxy sends `/music-api` requests to the local API at `http://127.0.0.1:5080` and removes the prefix. Do not put a public server address or credentials in the checked-in proxy configuration.

## Tests And Build

```powershell
npm run build
npm run test -- --watch=false
dotnet test backend/MusicPlayer.Api.Tests/MusicPlayer.Api.Tests.csproj
dotnet build backend/MusicPlayer.Api/MusicPlayer.Api.csproj
```

The Angular production files are emitted to `dist/music-player/browser`.

## Demo And GitHub Pages

The demo runs entirely in the browser, without the API or a music library:

```powershell
npm run start:demo
npm run build:demo
```

Open `http://localhost:4200` for the local demo. The build command produces
`dist/music-player/browser` with the `/music-player/` base path, ready to deploy
as a GitHub Pages artifact for this repository. In GitHub's **Settings > Pages**,
choose **GitHub Actions**. The included deployment workflow builds the demo and
publishes it when changes are pushed to `main`. The published URL is
`https://markostruna.github.io/music-player/`. Demo routes use URL hashes so
they also work when refreshed on GitHub Pages.

The demo signs in automatically as a fictional administrator; after signing out,
any email and password will sign back in. The albums and artists are fictional,
the artwork is included locally, and each album plays a short generated audio
sample. API responses and supported edits are simulated in memory and reset on
reload. Scans are simulated; external metadata refresh, remote image URLs, and
moving actual files are unavailable. No real accounts, music files, or backend
data are modified. Normal `npm start` and `npm run build` still use the real API.

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
