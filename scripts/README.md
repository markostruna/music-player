# Server scripts

PowerShell scripts for running the API on a Windows server as an [NSSM](https://nssm.cc) service behind XAMPP Apache.

Requirements: elevated (Administrator) PowerShell, `nssm.exe` on `PATH` (or pass `-NssmPath`), XAMPP, and the .NET SDK on the machine that runs `Deploy-Api.ps1` without `-SourcePath`.

## Configure-Api.ps1

One-time setup: creates the NSSM service and an Apache reverse proxy (`/music-api/` -> `http://127.0.0.1:5080/`).

```powershell
# Set up
.\Configure-Api.ps1 -InstallPath C:\server\music-player

# Set up with the first admin account (password: 12+ characters; only used when the Users table is empty)
.\Configure-Api.ps1 -InstallPath C:\server\music-player -BootstrapEmail admin@example.com -BootstrapPassword 'a-long-password'

# Remove service and Apache config (files are kept)
.\Configure-Api.ps1 -InstallPath C:\server\music-player -Remove

# Remove a service registered under another name, and delete its folder
.\Configure-Api.ps1 -InstallPath C:\old\folder -ServiceName AfterHoursApi -Remove -DeleteFiles
```

| Parameter | Default | Description |
|---|---|---|
| `-InstallPath` | required | Folder (not the .exe) where the API is installed. Database is stored in `data\`, logs in `logs\`. |
| `-ServiceName` | `MusicPlayerApi` | NSSM service name. |
| `-XamppPath` | `C:\xampp` | XAMPP location. |
| `-ApiUrl` | `http://127.0.0.1:5080` | Address the API listens on. |
| `-UrlPrefix` | `/music-api` | Public path served by Apache. |
| `-ApacheServiceName` | `Apache2.4` | Apache Windows service to restart. |
| `-BootstrapEmail`, `-BootstrapPassword` | none | Create the first admin on startup. Remove them afterwards (see below). |
| `-Remove` | off | Remove service and Apache config. |
| `-DeleteFiles` | off | With `-Remove`, also delete `-InstallPath`. |

`httpd.conf` is backed up before every change, and the Apache config is validated (`httpd -t`) before restart.

## Deploy-Api.ps1

Stops the service, copies binaries, starts the service and checks `/api/health`.

```powershell
# Publish from source and deploy
.\Deploy-Api.ps1 -InstallPath C:\server\music-player

# Deploy pre-built binaries
.\Deploy-Api.ps1 -InstallPath C:\server\music-player -SourcePath C:\publish

# Release with database changes: backup first, roll back automatically if the API doesn't start
.\Deploy-Api.ps1 -InstallPath C:\server\music-player -Migrate
```

The API applies EF Core migrations at startup. `-Migrate` backs up binaries and `data\` to `backups\<timestamp>` (last 5 kept) and restores both if the health check fails. `data\`, `logs\` and `backups\` are never overwritten by a deploy.

## Notes

- The API logs to `logs\stdout.log` (`stderr.log` is usually empty).
- NSSM environment changes apply only after `nssm restart MusicPlayerApi`.
- After the first admin exists, drop the bootstrap password from the service:
  ```powershell
  nssm set MusicPlayerApi AppEnvironmentExtra ASPNETCORE_ENVIRONMENT=Production Api__Url=http://127.0.0.1:5080 MUSICPLAYER_DATABASE_PATH=C:\server\music-player\data\music-library.db
  nssm restart MusicPlayerApi
  ```
- Settings such as metadata API keys are read from `%LOCALAPPDATA%\Afterhours\appsettings.json` of the account running the service (LocalSystem: `C:\Windows\System32\config\systemprofile\AppData\Local\Afterhours`).
