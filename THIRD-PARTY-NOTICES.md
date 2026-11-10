# Third-party components and distribution scope

The MIT license in this repository covers DroidNest's original client code, scripts, and `tools/SignWebView.java` only. It does not relicense Android, SDK tools, scrcpy, Chromium WebView, Java, or their dependencies.

## Public release assets and consent

Public client ZIPs contain the DroidNest executable, configuration, launcher/diagnostic/packaging scripts, the Java signing-helper **source**, and documentation. They do **not** contain Android images, SDK binaries, scrcpy binaries, WebView APKs, JDK binaries, phone data, ADB keys, signing keys, caches, or logs. A lone executable is not a complete first-run distribution.

The v0.2.0 client fetches upstream metadata to prepare an installation plan before component installation. The plan presents versions, sizes, origins, Android repository license text, and readable upstream license URLs. Explicit user consent is required before downloading/installing the runtime. Installation is for local use; neither consent, source URLs, checksums, nor a local installation receipt confer redistribution rights. Preserve upstream NOTICE/license files and review applicable agreements and open-source exceptions.

## Android SDK, emulator, and API 30 image

The installer obtains Android Emulator 37.2.13, Platform Tools 37.0.1, the Android 11/API 30 `default;x86_64` system image revision 11, and Build Tools 35.0.0 from Google's Android repository metadata and downloads. These selected versions and their official metadata are displayed before consent. Build Tools are used privately during local preparation, not installed into a global SDK or packaged in public assets.

The API 30 image is selected from official metadata at revision 11. The repository's legacy `runtime-origin.json` describes historical development files only; it is not installer authority and is not packaged in the public client. In that old local image, package.xml reported revision 11 while source.properties reported revision 10.

- SDK terms: https://developer.android.com/studio/terms
- Official SDK downloads: https://developer.android.com/studio
- Repository metadata: https://dl.google.com/android/repository/repository2-1.xml
- AOSP image metadata: https://dl.google.com/android/repository/sys-img/android/sys-img2-1.xml
- AOSP licenses: https://source.android.com/docs/setup/about/licenses

## scrcpy and dependencies

The installer downloads the official scrcpy 5.0.1 Windows archive and verifies its pinned SHA-256. scrcpy is Apache-2.0 licensed. The archive includes third-party libraries such as SDL, FFmpeg, libusb and Android platform tools, which retain their own terms. No scrcpy binaries are bundled in public DroidNest assets.

- Source/release: https://github.com/Genymobile/scrcpy/tree/v5.0.1
- License: https://github.com/Genymobile/scrcpy/blob/v5.0.1/LICENSE
- SDL license: https://libsdl.org/license.php
- FFmpeg licensing: https://ffmpeg.org/legal.html
- libusb license: https://github.com/libusb/libusb/blob/master/COPYING

## Chromium WebView

The installer obtains the x86_64 WebView 156.0.8078.47 artifact from LineageOS's official Git LFS endpoint, pinned to commit `a71893ba05acc77405e46262d458b47ab2b11e54` and its source SHA-256. Chromium and dependencies retain their own licenses. The original and locally re-signed APKs are not included in the repository or public ZIP.

- Artifact source: https://github.com/LineageOS/android_external_chromium-webview_prebuilt_x86_64/tree/a71893ba05acc77405e46262d458b47ab2b11e54
- Build/patch source: https://github.com/LineageOS/android_external_chromium-webview
- Chromium licensing: https://www.chromium.org/chromium-os/licenses/

## Private Java preparation runtime

Local WebView preparation uses Eclipse Adoptium Temurin JDK **21.0.13+9**, Windows x64, selected through Adoptium's release API. Its download metadata provides the archive origin, size and checksum. OpenJDK is licensed under GPLv2 with the Classpath Exception; bundled components may have additional notices. Java is used privately for preparation, without requiring a user-provided JDK or changing system Java settings.

- Project: https://adoptium.net/
- Release metadata: https://api.adoptium.net/v3/assets/release_name/eclipse/jdk-21.0.13%2B9
- Source: https://github.com/adoptium/temurin21-binaries/releases/tag/jdk-21.0.13%2B9
- GPLv2 with Classpath Exception: https://openjdk.org/legal/gplv2+ce.html

## Public AOSP test-key boundary

The Android development image uses the publicly known AOSP test certificate. With consent, local preparation obtains matching public AOSP test-key material and re-signs WebView using the packaged helper and Android apksigner. It preserves Android signature verification and does not modify user APKs. The test key is **not secret** and is **not a production trust boundary**. These images are unsuitable for sensitive production data; a newer WebView does not update the Android kernel or system security patch level.

- AOSP test keys and associated source: https://android.googlesource.com/platform/build/+/refs/heads/main/target/product/security/
- AOSP signing guidance: https://source.android.com/docs/core/ota/sign_builds

Downloaded signing material stays out of source control and release packages. The client executable itself is not Windows Authenticode-signed.

## Personal migration packages

`New-PortablePackage.ps1 -Mode Local` reads the installed runtime and its receipt, when present, from `%LOCALAPPDATA%\DroidNest`; complete legacy runtimes need no receipt. This is only for personal migration, not a public-distribution authorization. Private preparation tools, caches and signing keys are excluded. Phone data is excluded unless `-IncludeData` is explicit; when included it may carry Android/ADB identity, credentials and personal information. Keep these archives private, never attach them to GitHub Releases, and stop the source phone before copying. The target client asks for consent before importing the extracted runtime/data into its persistent directory.
