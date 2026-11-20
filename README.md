# CliTray

A Windows tray manager for command-line programs.

Keep the CLI tools you run all day — servers, tunnels, agents, watchers — in one list. Start, stop and watch them from a tray menu or a Fluent window instead of juggling terminal windows. The interface is in Simplified Chinese.

## Features

- **Managed items**: executable (PATH name or full path), arguments, working directory, optional log file, start delay, console visibility, stop timeout
- **Lifecycle**: per-item start / stop / restart, autostart on launch (with delay), stop all on exit
- **No leftovers**: children run inside Windows Job Objects and are killed when CliTray exits
- **Output**: live stdout/stderr capture (last 500 lines) plus log file tailing
- **Tray menu**: per-item state and controls, theme (system / light / dark), start with Windows, exit
- **Close to tray**: the close button hides the window; quitting goes through the tray menu
- **Single instance**: launching again signals the running instance to show its window

## Requirements

- Windows 10/11 with .NET Framework 4.8 (built into Windows 10 1903 and later)
- MSBuild from Visual Studio 2022 Build Tools (or a VS 2022 installation) to build

## Build

```bat
fetch-deps.cmd
build.cmd
```

Output: `bin\Release\CliTray.exe`.

`fetch-deps.cmd` downloads the third-party packages with NuGet into `packages\` and `lib\` (not committed to the repository):

- WPF-UI 4.3.0 and WPF-UI.Abstractions 4.3.0
- System.Memory / System.Buffers / System.Numerics.Vectors / System.Runtime.CompilerServices.Unsafe
- Newtonsoft.Json 13.0.3
- Microsoft.NETFramework.ReferenceAssemblies.net48 1.0.3 (the project compiles against these, so the .NET Framework 4.8 Developer Pack is not required)

## Files

- `src\Core` — config store, command resolution, process manager, job objects, autostart
- `src\Services` — tray icon, embedded assembly loading
- `src\Ui`, `src\Theming` — list row model, dialogs, Fluent styles

## Configuration

- Config: `%USERPROFILE%\.config\clitray\config.json` (`XDG_CONFIG_HOME` respected)
- State / crash log: `%USERPROFILE%\.local\state\clitray\`
