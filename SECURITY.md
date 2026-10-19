# Security

Tunero runs as administrator and changes Windows settings, so security problems matter more than in most apps.

## Reporting a vulnerability

Please do not open a public issue. Use **Report a vulnerability** on the repository's Security tab (GitHub private vulnerability reporting). If you cannot use GitHub, email security@tunero.dev. Include the app version (Settings > About), the Windows build and the steps to reproduce.

Fixes ship in a new release, and the report is credited in the release notes if you want.

## In scope

- Anything that lets a non-administrator gain administrator or SYSTEM rights through the app, for example through files or folders a normal user can write (the data folder, temp files, tools the app starts).
- Changes the app makes that it does not show in the confirmation dialog, or that Undo does not restore.
- Downloads or processes the app starts without checking where they come from (winget, PresentMon, PawnIO).
- Package managers the app starts for an update or the uninstall (Chocolatey runs elevated, only from a folder that only administrators can change).
- An update the app installs although its release signature (`Tunero.exe.sig`) is missing or does not match.
- Leaks of the VirusTotal API key.

## Known limits

- The .NET runtime reads some switches from the user's environment variables before any code of the app runs: a profiler (`CORECLR_ENABLE_PROFILING`, notification profilers), a diagnostic port it connects to (`DOTNET_DiagnosticPorts`, where a client can attach a profiler), EventPipe traces and crash dumps written to a chosen path. The app refuses to continue as administrator when one of them is set, but code loaded through them has run by then; .NET has no setting in the app itself that turns them off. Startup hooks (`DOTNET_STARTUP_HOOKS`) are turned off in the build.
- The storage analyzer moves files to the Recycle Bin through the Windows shell, which takes a path, not the file the app checked. The folders of the path stay held while the shell works, so none of them can be renamed or replaced by a link; a folder that a program of the user empties and turns into a mount point in that moment is only detected afterwards (the app then reports it, and the file is in the Recycle Bin, not deleted).

## Supported versions

Only the latest release gets fixes. It runs on Windows 11 25H2 and newer (x64); on 24H2 it starts only so that changes made there can still be undone.

The exe carries the .NET 10 runtime (supported by Microsoft until November 2028), so a .NET security fix that concerns the app ships as a new Tunero release. The in-app update installs only a release whose signature matches an update key built into the app: the release key, and a backup key kept offline in case the first is ever lost or leaked (every release since 2026-10-13 accepts both).
