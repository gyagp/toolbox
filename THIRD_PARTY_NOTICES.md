# Third-party notices

Toolbox's own code is MIT licensed. Bundled components retain their own licenses;
the project license does not relicense Microsoft or other third-party binaries.

The capture and media library features were migrated from Moment (Shike),
commit 099d2bacd2b352ca4a98597203da05febd8228e7. The original MIT copyright
notice is preserved in LICENSE. See docs/migration.md for the migration scope.

- **ScreenRecorderLib 7.0.1** — Copyright (c) 2017 Sverre Skodje, MIT.
  Source: https://github.com/sskodje/ScreenRecorderLib . Full license in
  `licenses/ScreenRecorderLib.txt`.
- **Windows App SDK / WinUI 3** — Microsoft and contributors.
  Source: https://github.com/microsoft/WindowsAppSDK and
  https://github.com/microsoft/microsoft-ui-xaml . Source portions are MIT;
  NuGet/runtime components may carry additional Microsoft terms.
- **.NET runtime** — .NET Foundation and contributors, MIT and third-party notices.
  Source: https://github.com/dotnet/runtime . Original runtime notices are retained
  in the release directory.
- **Microsoft Visual C++ runtime** — Microsoft Corporation; redistributable
  components under the Visual Studio license, not MIT.
  https://visualstudio.microsoft.com/license-terms/ . Only files from the installed
  Visual Studio `VC/Redist/MSVC/<version>/x64/Microsoft.VC*.CRT` directory are bundled.

The packaging script includes available upstream NuGet license/notice files under
`licenses/nuget/`. See `src/Toolbox.App/packages.lock.json` for pinned package versions.

**Reference project:** Snow Apps / Snow Shot by mg-chao informed the feature scope.
No Snow Apps source or assets are included. Snow Shot's GPL license does not apply
to this independent implementation: https://github.com/mg-chao/snow-apps/blob/main/LICENSE.md .
