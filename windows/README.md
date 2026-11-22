# Bema for Windows

Bema's presenter for PowerPoint on Windows: PowerPoint runs the slideshow on the audience display, Bema gives you your own presenter window, a prompter that follows your voice, pointer tools, coaching, a phone remote and polls. It's a port of the Mac app (`../Sources/Bema`), written in C# with WPF.

**Status: first version, not yet tried on a Windows PC.** It builds and its shared logic is tested in CI; nothing that drives PowerPoint, listens or draws over the slideshow has been run yet.

## Layout

- `src/Bema.Core/` — the logic shared with the Mac app's pure parts, with no Windows dependency: the prompter's script and speech matching (`Script.cs`), voice commands, pace and budgets (`Coach.cs`), the PPTX reader, speaker-notes writing (Claude, OpenAI, local models), the phone remote and poll server, and the MCP server for Claude Desktop. Builds and tests on any OS.
- `src/Bema.App/` — the Windows app (WPF): PowerPoint through COM on one STA thread (`PowerPoint/`), the presenter window, the detached prompter (hidden from screen sharing with `WDA_EXCLUDEFROMCAPTURE`), the click-through overlay that draws the pointer and effects over the slideshow, on-device speech (SAPI), and the notification-area icon.
- `tests/Bema.Core.Tests/` — xUnit tests for `Bema.Core`, many ported from the Mac app's tests so both apps behave the same.

## Build

You need the .NET 10 SDK. The app itself builds only on Windows; the shared logic and its tests build anywhere.

```bash
dotnet test tests/Bema.Core.Tests/Bema.Core.Tests.csproj
dotnet publish src/Bema.App/Bema.App.csproj -c Release -o publish   # Windows: one self-contained Bema.exe
```

CI (`.github/workflows/ci.yml`, job `windows`) runs both on every push and keeps `Bema.exe` as a build artifact for 14 days.

## What's different from the Mac app

- **PowerPoint only.** Keynote doesn't run on Windows.
- **Speech** uses Windows' desktop recognizer (SAPI), which runs on the PC for the speech languages installed in Windows. Bema never uses online recognition; when a language isn't installed it says so.
- **Not there yet:** the camera self view and bubble, the PowerPoint ribbon button (use Ctrl+Alt+P or right-click a deck in File Explorer → Present with Bema), automatic updates, a signed installer.
- **Where things are kept:** settings in `%APPDATA%\Bema\settings.json`; API keys encrypted for your Windows account (DPAPI) in `%APPDATA%\Bema\Keys`; reports and draft notes per deck under `%APPDATA%\Bema`; slide previews in the temp folder.

The privacy rules are the Mac app's (see the repository's README): nothing leaves the PC unless you turn it on, no audio is written to disk, and the prompter and presenter windows stay out of screen sharing unless you change that.
