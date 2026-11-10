# Third-party components and distribution scope

The MIT license in this repository covers DroidNest's original client code and scripts only. It does not relicense Android, Android SDK tools, scrcpy, Chromium WebView, Java, or their dependencies.

## Public release assets

Public releases contain the DroidNest client executable, configuration, preparation/diagnostic scripts, and documentation. They do **not** contain Android images, Android SDK binaries, scrcpy binaries, WebView APKs, phone data, ADB keys, signing keys, or logs. A client executable alone cannot run APKs.

## User-provided Android runtime

The local import script copies an SDK already available to the user. It does not accept licenses on the user's behalf or grant redistribution permission. Android SDK component agreements and their applicable open-source license exceptions must be reviewed separately. Preserve the component NOTICE/license files.

- Android SDK terms: https://developer.android.com/studio/terms
- AOSP licenses: https://source.android.com/docs/setup/about/licenses

## scrcpy and dependencies

The component preparation script downloads the official scrcpy 5.0.1 Windows archive and verifies its SHA-256. scrcpy is Apache-2.0 licensed. Its archive includes separate third-party libraries, including SDL, FFmpeg and Android platform tools; each retains its own terms. No scrcpy binaries are bundled in public DroidNest assets.

- Source and release: https://github.com/Genymobile/scrcpy/tree/v5.0.1
- License: https://github.com/Genymobile/scrcpy/blob/v5.0.1/LICENSE

## Chromium WebView

The preparation script fetches the x86_64 WebView artifact through LineageOS's official Git LFS endpoint, pinned to commit `a71893ba05acc77405e46262d458b47ab2b11e54`, and verifies the source hash. Chromium and its dependencies retain their licenses. The artifact is not included in this repository or public releases.

- Artifact source: https://github.com/LineageOS/android_external_chromium-webview_prebuilt_x86_64/tree/a71893ba05acc77405e46262d458b47ab2b11e54
- Build/patch source: https://github.com/LineageOS/android_external_chromium-webview
- Chromium licensing: https://www.chromium.org/chromium-os/licenses/

The current local development image uses the publicly known AOSP test certificate. The optional local WebView preparation re-signs with that matching public test key; it does not disable Android signature verification. Test-key-signed images are unsuitable for sensitive production data and are not production-signed products. The downloaded key material is excluded from source control and public assets.

## Personal migration packages

`New-PortablePackage.ps1 -Mode Local` is for the user's personal migration of an existing local runtime. It is not a public-distribution authorization. Such archives must not be attached to GitHub Releases. Phone data is excluded unless explicitly requested and may contain credentials or personal information.
