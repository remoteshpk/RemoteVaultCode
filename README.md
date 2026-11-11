# RemoteVault

RemoteVault is a free, open-source Windows backup application. It backs up folders and databases to your own object storage, restores them by selection or by point in time, and runs on a schedule without anyone logged in to the app.

It runs on both Windows client and Windows Server (x64), which is why it is built on WPF rather than WinUI.

There is no license key, no activation and no account. The app only talks to the storage you configure.

## Features

- **File backup** to S3-compatible storage or Azure Blob, as a full run or for selected folders. Unchanged files are skipped, including files that already exist in the bucket after a reinstall.
- **File restore** by browsing the remote backup tree, by picking a version of a file, or by restoring a snapshot as of a timestamp.
- **Database backup** for Microsoft SQL Server (full and differential), MySQL and PostgreSQL.
  The local folder for database backup files must be directly on a drive (for example `C:\RemoteVaultDB`), not inside a user profile such as Documents or Downloads: SQL Server writes the file under its own service account, which has no access there.
- **Scheduling** through Windows Task Scheduler: hourly intervals or daily for files; daily, weekly or monthly for databases.
- **Storage cleanup** from Settings or the command line: delete old remote versions, keeping the newest *n* of each file (at least 5). The app shows how many versions and how much space will go and asks for confirmation before deleting anything.
- **Protected configuration**: storage credentials are encrypted with Windows DPAPI for the current user.

### Storage providers

Remote, Wasabi, Amazon S3, Azure Blob, Contabo and Hetzner, each with a list of regional endpoints to choose from in Settings.

## Install

Download the installer from [remotevault.app](https://www.remotevault.app/rvapp/RemoteVault.exe) and run it. It installs per machine and needs administrator rights.

On first start, Settings opens unlocked so you can enter the storage connection and the folders to protect. After that, Settings opens locked so nothing changes by accident; select **Edit settings** to make changes.

### Upgrading from a licensed version (7.x or earlier)

Install the current version over the existing installation. Your configuration, backup history and manifests are kept, and the license key stored in the configuration is ignored and dropped the next time settings are saved. Going back to a licensed version afterwards means configuring it again.

## Command line

| Flag | Effect |
|---|---|
| `--config` | Open with Settings already unlocked for editing |
| `--configbackup` | Open with the Databases page already unlocked for editing |
| `--run-scheduled-backup` | Run a file backup without a window and exit with the result code |
| `--run-scheduled-db-backup` | Run a database backup without a window (full or differential, as scheduled) |
| `--run-scheduled-db-full-backup` | Force a full database backup |
| `--run-scheduled-db-diff-backup` | Force a differential database backup (SQL Server only) |
| `--clean-old-versions <n>` | Delete old remote versions of backed-up files, keeping the newest `n` per file (5 to 100) |

## Where data is stored

Everything lives under `%LocalAppData%\RemoteVault\`:

| Path | Contents |
|---|---|
| `remotevault.config.json` | Main configuration; credentials are DPAPI-encrypted |
| `data\db_profiles.json` | Database backup profiles |
| `data\backup-state\*.json` | Backup status, history and operation history |
| `data\backup-state\manifests\` | One SQLite manifest per bucket, used to detect changed files |

The configuration file is plain JSON and can be edited by hand. Only the storage access key and secret key are encrypted, and only for the Windows user who saved them: if you copy the file to another machine or user, the rest of the settings load and you re-enter the keys. You can also type a key into the file as plain text; the app encrypts it the next time it starts.

## Build from source

Requirements: Windows and the .NET 8 SDK.

```powershell
# Build
dotnet build RemoteVault.slnx -c Release -p:Platform=x64

# Self-contained build that runs without .NET installed
dotnet publish RemoteVault.Desktop.WPF -c Release -r win-x64 --self-contained
```

### Solution layout

| Project | Role |
|---|---|
| `RemoteVault.Contracts` | DTOs, enums and events; no dependencies |
| `RemoteVault.Infrastructure` | Log category constants |
| `RemoteVault.Core` | All backup, restore, storage, scheduling and configuration services |
| `RemoteVault.Desktop.WPF` | The WPF application; builds `RemoteVault.exe` |

## License

MIT. See [LICENSE](LICENSE).
