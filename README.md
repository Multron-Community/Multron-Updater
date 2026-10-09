# Multron Updater

**Multron Updater** keeps a Windows program up to date straight from a GitHub repository.
It watches a file or folder in a repo, and when it changes it downloads the new version,
closes the running program, replaces the files and starts the program again — automatically,
from the system tray.

Built with C# / WPF on .NET 8.

---

## Features

- **Multiple targets** – add as many repo → program pairs as you like, each with its own settings. Enable, duplicate or remove them from the list.
- **GitHub as the update source** – pick any owner, repository, branch and path (a single file or a whole folder).
- **After-update actions per target** – start the program again and/or refresh the active Microsoft Edge tab (F5).
- **Windows and console programs** – start arguments, and for console apps: show the window, keep it open after exit, or run hidden with the output written to a log file. Console apps are closed with Ctrl+C before an update.
- **Paste-a-link setup** – paste a GitHub link and the fields fill themselves in.
- **Safe update flow** – download → verify → close the program → replace files → restart.
  If replacing fails, the previous version is restored automatically.
- **Only changed files are downloaded** – local files are compared with GitHub's blob hashes (the same hash `git` uses).
- **Automatic updates (optional)** – turn it on with one switch and choose how often to check (in minutes).
- **Runs in the background** – closing the window keeps it running in the system tray.
- **Download animation** – a popup slides in at the bottom-right, the taskbar button shows a progress bar and the tray icon spins while an update downloads.
- **Log system** – color-coded log levels (Info / Success / Warning / Error) with filtering and search, also written to a log file.
- **Backups** – replaced files from the last 3 updates are kept.
- **Private repositories** – optional personal access token, stored encrypted (Windows DPAPI) for the current user.
- **Start with Windows** – registered as a logon task, so it starts without a UAC prompt.
- **Auto-saving settings** – every change is saved as you type.
- **Updates itself** – every push to `main` builds a new release, and Multron Updater installs it and restarts.

## Requirements

- Windows 10 / 11 (x64)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- Administrator rights (the app asks for them on start, so it can close and replace programs that run elevated)

## Getting started

1. Download `MultronUpdater.exe` from the [latest release](https://github.com/Multron-Community/Multron-Updater/releases/latest) and run it.
2. In the **Targets** tab, select a target (or click **+ Add**) and give it a name.
3. **GitHub source** – paste a link such as
   `https://github.com/user/repo/blob/main/bin/Release/App.exe` and click **Fill from link**,
   or enter *Owner*, *Repository*, *Branch* and *Path in repository* by hand.
4. **Program on this computer** – choose the target folder and, optionally, the `.exe` that
   should be closed while the files are replaced.
5. **After updating** – choose whether to start the program again and/or refresh the active Edge tab.
6. Click **Check this target** to run the first update. Repeat for more targets.
7. In the **General** tab, turn on **Update automatically** to keep every enabled target updated in the background.

### Console programs

In **Program on this computer**, choose **Console app** (it is detected automatically when you pick the
`.exe` with **Browse...**) and, optionally, **Start arguments** such as `--port 8080 --silent`. Then choose
how the console window behaves:

| Option | What happens |
|---|---|
| Show the console window | The program opens in its own console window. |
| Show it and keep it open after the program exits | Runs inside `cmd /k`, so the window stays open to read errors. |
| Run hidden in the background | No window; output and errors go to `%AppData%\MultronUpdater\console-logs\<target>.log` (**Open console log**). |

Before replacing files, Multron Updater sends **Ctrl+C** so the program can shut down cleanly and terminates it
only if it is still running after 5 seconds. Use **Start now** to try the settings without waiting for an update.

### File groups

Turn on **Update only the checked files, in groups** to pick exactly which files are updated:

- **Load from GitHub** lists the files under the path and marks them: yellow `•` changed, green `+` new,
  grey `✓` up to date, red `−` not found on GitHub.
- **Drag files or folders from File Explorer** onto a group (or use **Add files...**). They must be inside
  the target folder. Dropping a file that is already listed onto a group moves it there.
- Create groups with **+ New group**, rename them by clicking the name, and drag files between groups
  (or right-click a file → **Move to group**).
- Each group has its own **Enabled**, **Restart the program** and **Refresh Edge (F5)** options. A group's
  actions run only when one of its files changed.

### Refreshing Microsoft Edge

With **Refresh the active Microsoft Edge tab (F5)** enabled, Multron Updater brings the most
recently used Edge window to the front after the update, presses F5 and then gives focus back
to the window you were using. This is handy when the target is a web page or a local web app.

### File or folder?

| Path in repository | What is synced |
|---|---|
| `bin/Release/App.exe` | Only that file |
| `bin/Release` | Every file inside the folder (sub-folders included) |

> **Tip:** For .NET apps that are not published as a single file, the `.exe` is only a small
> launcher and the real code lives in the `.dll` next to it. Use the **folder** path in that case.

## How it works

1. Asks GitHub for the latest commit of the branch. This request uses an ETag, so when nothing
   has changed it does not count against GitHub's rate limit.
2. If the commit changed, reads the file list and compares each file with the local copy.
3. Downloads the changed files (pinned to the exact commit) and verifies their hashes.
4. Closes the program (gracefully first, forcefully after 5 seconds).
5. Backs up and replaces the files, then runs the after-update actions (restart the program, refresh Edge).

Targets are checked one after another.

## Where things are stored

| What | Location |
|---|---|
| Settings | `%AppData%\MultronUpdater\settings.json` |
| Log file | `%AppData%\MultronUpdater\log.txt` |
| Backups | `%AppData%\MultronUpdater\backups\<target id>\` |
| Console output (hidden console apps) | `%AppData%\MultronUpdater\console-logs\` |

## GitHub rate limit

Without a token GitHub allows 60 API requests per hour. Unchanged checks are free (HTTP 304),
so a 5-minute interval works fine. If you hit the limit, or the repository is private, add a
personal access token (read-only *Contents* permission is enough).

## Updating Multron Updater itself

1. A push to the `main` branch starts the **Build and release** GitHub Actions workflow
   (`.github/workflows/release.yml`). Pushes that only change `*.md`, `LICENSE` or `.gitignore` are skipped.
2. The workflow builds the single-file exe and publishes it as a release named `v<major>.<minor>.<run number>`.
   Major and minor come from `<Version>` in `MultronUpdater.csproj`, so change it there to start a new series (e.g. `1.2.0`).
3. Multron Updater checks the latest release 20 seconds after it starts and then every 3 hours
   (or on **General → Check for updates**). When a newer version exists it downloads it, checks its size
   and SHA-256, swaps its own exe and restarts in the same state (window open or in the tray).

Turn off **Update Multron Updater automatically** in the **General** tab to only get a notification
and install the update with the **Install** button. Development builds (running from `bin\`) never update themselves.

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer).

```bat
build.bat
```

`build.bat` publishes a single-file exe to `publish\MultronUpdater.exe`.
If Multron Updater is running, it closes it first (it may ask for administrator permission).

Or manually:

```bat
dotnet publish -c Release -o publish
```

> Do not publish into a folder that contains the project folder — the SDK then excludes the
> source files from compilation and produces a broken exe.

## Limitations

- Files deleted from the repository are not deleted locally.
- Files stored with Git LFS are not supported.
- Very large repositories (100,000+ files) may return a truncated file list from GitHub.
