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
- **Local repository folder (no internet)** – update from a copy of the repository on disk (a clone, a USB drive, a network share) with the same safe install flow.
- **After-update actions per target** – start the program again and/or refresh the active Microsoft Edge tab (F5).
- **Windows and console programs** – start arguments, and for console apps: show the window, keep it open after exit, or run hidden with the output written to a log file. Console apps are closed with Ctrl+C before an update.
- **Multron server maintenance** – for a Multron / VirusKov cloud scan server target, the server is switched to its "Updating server" maintenance mode through its local dashboard before it is closed: clients are told the server is being updated and scans in progress finish first. The updated server starts with scans open again.
- **Multron server signature reload** – when an update changes no program files (.exe / .dll), the files are replaced while the server keeps running and its signatures are reloaded through its dashboard instead of restarting it. If a file is held open by the server, it is closed and started as usual.
- **Multron server seamless update** – the server is not closed for the update. Files it holds open (its exe) are renamed to `*.old-<time>`, the new files are put in place and the new version is started with `--handoff` next to the running server. The running server keeps scanning in its "Getting updated" mode while the new one loads its engine; then it finishes the scans in progress (up to the "wait up to … s for scans" setting), hands its ports over and exits, and the new version is scanning again within about a second. If the new version does not take over (it crashed, or did not load within `ServerSeamlessMinutes`, 20 by default), the previous files are put back and the running server stays as it was. A server too old for it is updated the usual way once; the renamed `*.old-*` files are deleted after the switch (or on the next update).
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

### Update source

Each target chooses where updates come from (**GitHub source → Update source**):

| Option | What it does |
|---|---|
| **Repository files + releases** (default) | Files under *Path in repository* are kept in sync with the branch, so a push is seen right away. New releases install the files the repository does not have; a file that exists in both is taken from the repository. |
| **Repository files only** | Raw files from the branch. Each file is checked with its git hash; if a `<file>.sha256` sits next to it in the repository, the SHA-256 is verified too. |
| **Releases only** | Installs the file(s) of the latest GitHub release, verified with the SHA-256 GitHub publishes for every asset. |
| **Local repository folder (no internet)** | Reads *Path in repository* from a copy of the repository on disk instead of GitHub. Nothing is downloaded and owner, repository, branch and token are not used. |

#### Local repository folder (no internet)

For a computer without internet access (for example a Multron / VirusKov scan server), choose
**Local repository folder (no internet)** and pick the root of the repository copy, e.g.
`C:\Users\me\GitHub\HydraDragonAntivirus`, `E:\HydraDragonAntivirus` on a USB drive or
`\\MAINPC\share\HydraDragonAntivirus`. *Path in repository* is read from that folder (a file or a folder;
empty = the whole folder), so `OpenMalwareScannerPortable` keeps a Multron server install in sync.

- Files are compared with SHA-256; only new or changed files are copied. A file you changed in the
  target folder is put back. When the copy's names, sizes and times have not changed since the last
  check, nothing is hashed, so frequent automatic checks stay cheap.
- The changed files are first copied into a staging folder and checked again, so a file that changes
  in the repository copy while it is being copied is not installed. A `<file>.sha256` next to a file
  in the copy is verified, and Git LFS pointers (a clone without `git lfs pull`) are refused.
- `.git` folders are skipped. Nothing is ever deleted from the target folder.
- Everything else is the same as for GitHub updates: *Update only the checked files* and file groups,
  Multron server maintenance and signature reload, closing and restarting the program, backups and
  automatic rollback.
- If the folder is missing (the USB drive or share is not connected), the check fails with that
  message and the installed files are left alone.

**Release file(s) to install** takes a file name, wildcards (`MyApp-*-win64.zip`) or several names separated by `;`.
When it is empty, the name of the program (or of the file in *Path in repository*) is used. A single `.exe` asset is installed
under the program's name, and `.zip` assets are extracted into the target folder (a single top folder inside the zip is skipped).
**Show latest release** lists the files of the latest release and marks the ones that match.

Checking for a new commit or a new release tag does not use the GitHub API, so frequent checks do not run into the
60 requests/hour limit; the API is only called when something actually changed.

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

### Versioned program names (`*`)

Some programs put the version in the file name (`ZapZap-7.5.1-windows-x86_64.exe`). Write `*` in place of
the version in **Program to close** (`ZapZap-*-windows-x86_64.exe`): every running copy that matches is
closed, the new release is installed under its own name, the previous version's exe is removed (and kept
in the backup) and the new one is started.

You do not have to type it: **Show latest release** lists the release files with a **Use this file** button,
**Running...** lists the running programs (and everything running from the target folder, such as a hidden
server), and **Browse...** picks a file. All three fill in the fields with `*` in place of the version.

### Build from source (optional)

Turn on **Build the program from the downloaded source before installing it** to compile the program on
this computer instead of downloading a ready-made exe:

1. The repository (leave *Path in repository* empty for the whole repository) or the local repository
   folder is kept in a **source folder** (default `%AppData%\MultronUpdater\sources\<target id>`).
2. Your **build commands** run there with the compilers installed on this computer, in Command Prompt,
   Windows PowerShell or PowerShell 7. All lines run in one shell, so `cd`, `set` and variables carry over.
   **Stop at the first command that fails** checks the exit code after every complete command.
3. The files of the **output folder** that differ from the installed ones are installed into the target
   folder with the usual flow (close, back up, replace, restart).

A failed or timed-out build never touches the installed program; automatic checks do not build the same
failing version again (a manual check or a new commit does). Templates are included for .NET, MSBuild,
Node.js, PyInstaller, Rust, Go, CMake and repository build scripts, and **Detect** picks one from the files in
the source folder. The commands can use `%MU_SOURCE%`, `%MU_OUTPUT%`, `%MU_TARGET%`, `%MU_VERSION%`, `%MU_EXE%`
and `%MU_TARGET_NAME%` (`$env:MU_SOURCE` in PowerShell) plus your own environment variables.
**Build and install now** builds even when the source has not changed.

### Theme

**General → Application → Theme**: follow the Windows setting, or always light or dark.

### Multron server options (developers)

The Multron server options (maintenance mode, signature reload, seamless update) are hidden unless
**General → Multron developers → Show Multron server options** is on. The server dashboard address and the
scan wait time set there are used by every target that keeps **Use the global server settings**.

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
| Source folders (build from source) | `%AppData%\MultronUpdater\sources\<target id>\` |
| Build logs | `%AppData%\MultronUpdater\build-logs\` |

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
