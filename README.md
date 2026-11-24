# Asker

Asker is a small Windows tray app. Copy some text or an image, press a hotkey, and an AI model answers in an always-on-top window.

- Hotkeys each with their own prompt and model (for example: explain, translate)
- Text or image from the clipboard
- Answers stream in; several can run at once
- History saved encrypted on your PC, with search, pinning, renaming, rerun and delete
- Light and dark theme, optional hiding from screen sharing, optional proxy

Answers come from Nous Portal by Nous Research; you need your own API key.

## Download

Get `Asker.exe` from [Releases](https://github.com/senko-labs/asker/releases). It runs on 64-bit Windows 10/11 and needs no install or .NET runtime.

## Build

Requires the .NET 8 SDK.

```
dotnet build
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
```

Release files are built from this repository by GitHub Actions ([release workflow](.github/workflows/release.yml)) when a version tag such as `v1.3.0` is pushed.

## Privacy

Asker sends data over the network only when you ask it to:

- When you press an ask hotkey (or Rerun), the clipboard text or image and that hotkey's prompt are sent to the Nous Portal API (`inference-api.nousresearch.com`) to get the answer.
- When you open Settings with an API key entered, Asker asks the same API for its list of models.
- If you set a proxy in Settings, these requests go through it.

Nothing else is sent anywhere: no telemetry, analytics or update checks. Your API key, proxy password and history (questions, images and answers) are stored only on your PC under `%AppData%\Asker`, encrypted with Windows DPAPI so only your Windows account can read them. Unpinned history can be deleted automatically after a period you choose in Settings.

## Code signing policy

<!-- Add after SignPath Foundation approves the project:
Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).
-->

Release files are built by GitHub Actions from this repository and are signed only after approval.

Team roles:

- Committers and reviewers: [senko-labs](https://github.com/senko-labs)
- Approvers: [senko-labs](https://github.com/senko-labs)

Team members use multi-factor authentication for GitHub and for signing approval.

## License

[MIT](LICENSE)
