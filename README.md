ONLY for Nexus Admins, This code is copyright protected by a EULA, do not steal, repo goes private once approved.

# Cyberpunk 2077 VR - RA01 Edition (source)

A free VR conversion for Cyberpunk 2077 (game build 2.31): stereo OpenXR rendering, 6DoF motion controls, full-body IK and physical interactions (aiming, reloading, throwing, melee, driving, climbing, swimming).

Written in C# and compiled ahead of time with .NET 8 NativeAOT, so players need no .NET installed. The mod has no networking or telemetry, and nothing runs outside the game session.

Download for players: Nexus Mods, by Rob A. (007xSlayerx007).

## What gets built

| File | Project | Purpose |
|---|---|---|
| `RA01.Launcher.exe` | `Launcher\RA01.Launcher.csproj` | Checks for SteamVR and OpenXR, places the doorman, starts the game, then starts the mod inside it |
| `RA01.dll` | `RA01.csproj` | The mod itself (folders `Core`, `Actions`, `Visuals`) |
| `xinput9_1_0.dll` | `Launcher\Doorman\RA01.Doorman.csproj` | The "doorman": a small loader the launcher places in the game's `bin\x64` folder for the session and removes afterwards. An `xinput9_1_0.dll` from another mod is never touched |
| `openxr_loader.dll` | (prebuilt) | The official Khronos OpenXR loader, unmodified (Apache-2.0) |

## Build (Windows 10/11 x64)

1. Install the **.NET 8 SDK**.
2. Install **Visual Studio 2022** (or its Build Tools) with the **Desktop development with C++** workload. NativeAOT needs its linker.
3. Clone this repository.
4. In PowerShell, from the repository folder:

   ```powershell
   .\build.ps1
   ```

5. The folder `Cyberpunk2077-VR-RA01-Edition\` now holds the same files as the release on Nexus. Run `RA01.Launcher.exe` from there.

Close the game before building: it locks `RA01.dll` while it runs.

## Tests

```powershell
dotnet test Tests\RA01.Tests.csproj
```

109 xUnit tests cover the settings file, the hook table, controls, VRIK maths and the code rules below. They never touch the game or your real Settings and Logs folders.

## Layout and code rules

- `Core` - the VR lifecycle, hooks into the game, rendering, settings. Core never calls into Actions or Visuals; `Core\Entry.cs` is the one start-up file that switches them on.
- `Actions` - player mechanics, driven by Core's tracking data.
- `Visuals` - body and hand visuals (VRIK), reacting to Actions.
- `Launcher` - the launcher and the doorman. Runs outside the game.
- Every comment block starts with `RA01`, and every class and function comment ends with its CPU and GPU cost (`Cost - CPU: ...; GPU: ...`). `Tests\CodeRulesTests.cs` checks both.

## License

See `EULA.txt`. The source is public for viewing, education and review only: no redistribution, no derivative works and never sold. Cyberpunk 2077 is a trademark of CD PROJEKT S.A. This is an unofficial fan modification, not affiliated with or endorsed by CD PROJEKT.
