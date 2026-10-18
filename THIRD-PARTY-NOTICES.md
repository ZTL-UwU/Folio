# Third-party notices

Folio ships with the components below. The build copies this file and the license texts into the `licenses` folder next to `Folio.exe`, so they go out with every build.

## PDFium

- Package: `bblanchon.PDFium.Win32` 156.0.8076, built from PDFium branch `chromium/8076`.
- License: BSD-3-Clause, plus Apache-2.0 for parts of the code. Full text: [`licenses/PDFium/pdfium.txt`](licenses/PDFium/pdfium.txt).
- The binaries are built by Benoît Blanchon's pdfium-binaries (<https://github.com/bblanchon/pdfium-binaries>), MIT: [`licenses/PDFium/pdfium-binaries.txt`](licenses/PDFium/pdfium-binaries.txt).

`pdfium.dll` has these libraries from the PDFium tree built in. Their license texts are in [`licenses/PDFium`](licenses/PDFium), copied verbatim from the `licenses` folder of the pdfium-binaries `chromium/8076` release for Windows, whose `pdfium.dll` is identical to the one in the NuGet package; that folder lists what the build links.

| Library | License | Text |
| --- | --- | --- |
| Abseil | Apache-2.0 | `abseil.txt` |
| Anti-Grain Geometry 2.3 | AGG license (permissive) | `agg23.txt` |
| fast_float | MIT | `fast_float.txt` |
| FreeType | FreeType License | `freetype.txt` |
| ICU | Unicode License v3 | `icu.txt` |
| Little CMS | MIT | `lcms.txt` |
| libjpeg-turbo | IJG License, BSD-3-Clause, zlib | `libjpeg_turbo.ijg`, `libjpeg_turbo.md` |
| OpenJPEG | BSD-2-Clause | `libopenjpeg.txt` |
| libpng | PNG Reference Library License v2 | `libpng.txt` |
| LLVM | Apache-2.0 with LLVM Exceptions | `llvm-libc.txt` |
| simdutf | MIT | `simdutf.txt` |
| zlib | zlib License | `zlib.txt` |

When PDFium is updated, take the `licenses` folder from the pdfium-binaries release of the same build again.

## .NET

- The .NET 10 runtime, compiled into `Folio.exe` and the File Explorer thumbnail handler with Native AOT.
- License: MIT, plus the third-party notices of the libraries the runtime includes. The build copies both from the .NET runtime pack it used to `licenses/dotnet-LICENSE.txt` and `licenses/dotnet-THIRD-PARTY-NOTICES.txt`.

## Windows SDK projection (C#/WinRT)

- Package: `Microsoft.Windows.SDK.NET.Ref` 10.0.26100.87, the .NET projection of the Windows APIs with the C#/WinRT runtime (`WinRT.Runtime`), compiled into `Folio.exe`.
- License: the Windows SDK license terms, <https://aka.ms/WinSDKLicenseURL>. C#/WinRT itself is MIT (<https://github.com/microsoft/CsWinRT>).

## Windows App SDK

- Packages: the Windows App SDK 2.5.1 components `Microsoft.WindowsAppSDK.Runtime`, `.WinUI`, `.Foundation`, `.InteractiveExperiences` and `.Base`. The unpackaged build deploys them self-contained (their runtime DLLs ship in the app folder); the Store installs them with the MSIX package as a framework package.
- License: Microsoft Software License Terms for the Windows App SDK, plus Microsoft's third-party notices. The build copies both from the NuGet package to `licenses/WindowsAppSDK-license.txt` and `licenses/WindowsAppSDK-NOTICE.txt`. Before distributing, check the distributable-code section of those terms.
