# Third-party notices

Folio ships with the components below. The build copies this file and the license texts into the `licenses` folder next to `Folio.exe`, so they go out with every build.

## PDFium

- Package: `bblanchon.PDFium.Win32` 156.0.8076, built from PDFium branch `chromium/8076`.
- License: BSD-3-Clause, plus Apache-2.0 for parts of the code. Full text: [`licenses/PDFium-LICENSE.txt`](licenses/PDFium-LICENSE.txt), copied verbatim from the PDFium source at that branch.
- The NuGet packaging is by Benoît Blanchon under Apache-2.0 (<https://github.com/bblanchon/pdfium-binaries>).

`pdfium.dll` also statically links third-party libraries from the PDFium tree, such as FreeType, libjpeg-turbo, libpng, zlib, Little CMS, OpenJPEG and Abseil. **Their notices still need to be collected before a public release.** Take them from `third_party/*/LICENSE*` in the PDFium checkout at `chromium/8076`, and check the build arguments in pdfium-binaries to see which ones are actually linked.

## Windows App SDK

- Packages: the Windows App SDK 2.5.1 components `Microsoft.WindowsAppSDK.Runtime`, `.WinUI`, `.Foundation`, `.InteractiveExperiences` and `.Base`, deployed self-contained (their runtime DLLs ship in the app folder).
- License: Microsoft Software License Terms for the Windows App SDK, plus Microsoft's third-party notices. The build copies both from the NuGet package to `licenses/WindowsAppSDK-license.txt` and `licenses/WindowsAppSDK-NOTICE.txt`. Before distributing, check the distributable-code section of those terms.
