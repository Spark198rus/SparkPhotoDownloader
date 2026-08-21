# Spark Photo Downloader

A photo and video organiser for Windows, Linux and macOS, modelled on
[Rapid Photo Downloader](https://damonlynch.net/rapid/). It copies photos and videos off
cameras, phones, memory cards and folders, generating destination subfolders and file names
from the shot's own metadata — and it remembers every file it has ever downloaded.

Built with **Avalonia 12**, **.NET 10** and **C#**.

## The window

The layout follows Rapid Photo Downloader's: input decisions on the left, an overview of the
files in the middle, output decisions on the right.

| Left | Centre | Right |
| --- | --- | --- |
| **Devices** — cameras, phones and memory cards, detected automatically, each with a capacity bar | **Thumbnails** — every photo and video found, with a checkbox, a type chip and a status badge | **Photos** — destination folder, subfolder scheme, rename scheme, live example, folder tree |
| **This Computer** — any folder you add, including its subfolders | Filter (`Show`), sort, thumbnail size, and check All / None / New only / Photos / Videos | **Videos** — the same again, below a splitter |
| **Timeline** — shots grouped by the gap between them, from 5 minutes to 24 hours | Live download progress | Job Code, backup, copy/move, and the Download button |

## Remembering what was downloaded

This is the part that goes beyond Rapid Photo Downloader.

Every download is recorded in a SQLite database, keyed the way Rapid Photo Downloader keys
it: **original file name + modification time + size**. That triple survives renaming at the
destination, re-inserting the same card, and a camera clock that is a whole time zone out
(offsets are detected at 60, 30 and 15 minute resolutions).

On every scan each file is looked up, and the copy it produced is checked:

| State | Badge | Appearance | Checked for download? |
| --- | --- | --- | --- |
| Never downloaded | — | full colour, blue border | yes |
| Downloaded, copy still there | grey `DOWNLOADED` | dimmed to 42% | no |
| **Downloaded, copy no longer at the destination** | **amber `MISSING`** | **amber border, amber wash, full brightness** | **no** |
| Downloaded this session | green `DONE` | — | no |
| Failed this session | red `FAILED` | — | no |

A file whose copy was deleted after downloading is **not** downloaded again. It is flagged,
and it stays flagged:

- an amber `MISSING` badge, border and wash on the thumbnail — deliberately *not* dimmed, so
  it stands out rather than receding like an ordinary already-downloaded file;
- a hover tooltip giving the date it was downloaded, the exact path it was written to, and
  what to do about it;
- a count in the toolbar summary, and an amber chip in the status bar with **Show** and
  **Forget** buttons;
- a `Missing at destination` entry in the `Show` filter;
- a `Missing` row in the Download history window.

To fetch one again you have to say so explicitly, through any of:

- right-click a thumbnail → **Forget download history (allow re-download)**;
- the status-bar chip's **Forget**, or **Forget all missing** in the history window;
- Preferences → **Also re-download files whose copy has gone missing from the destination**,
  which turns the flag back into an ordinary download.

If the volume holding the copy is simply not mounted — an external archive drive, a NAS share
that is offline — the file is reported as *drive offline*, not as deleted, so unplugging a
drive never makes a whole library look missing. **Re-check destinations** in the toolbar
re-runs the check after you plug it back in.

## Everything else it does

**Sources**
- Removable volumes detected automatically and polled every three seconds, classified by the
  camera folders they carry (`DCIM`, `AVCHD`, `PRIVATE`, `M4ROOT`, `XDROOT`, …).
- Capacity bar, file-system type and photo/video counts per source.
- Any folder can be added as a source through *This Computer*; subfolders are included.
- Checking a source checks everything found on it — except files already downloaded.

**Previews**
- JPEG, PNG, TIFF, WebP, HEIC and friends decoded directly.
- Raw files (CR2/CR3, NEF, ARW, ORF, RAF, RW2, DNG, PEF, X3F, IIQ, 3FR, GPR and more) are not
  demosaiced: the JPEG preview the camera embedded is extracted and used, which is both far
  faster and what the camera itself would show. The extractor stops as soon as it has a
  preview wide enough for the grid — a couple of megabytes into a 36 MB frame — instead of
  reading to the end of the file, and validates each candidate's frame header, because raw
  sensor data contains JPEG start and end markers by coincidence and those false positives
  are frequently larger than the real preview.
- Videos use a `.THM` sidecar when the camera wrote one, then an embedded poster frame, then
  a frame pulled with `ffmpeg` if it is on `PATH`.
- Exif orientation is applied, and thumbnails are cached on disk keyed by path + mtime + size.

**Naming**
- Subfolder and file-name schemes built from date/time (26 formats), original file name, image
  number, camera make/model/serial, lens, aperture, ISO, exposure, focal length, shutter
  count, video codec/size/frame rate/duration, Job Code, device name, sequence numbers
  (downloads today, session, stored, per-subfolder) and sequence letters (a…z, aa…zz).
- Built-in presets matching Rapid Photo Downloader's, plus a preset editor for your own.
- A live example under each dropdown, and a warning when a scheme cannot guarantee unique
  names.
- The destination folder tree shows existing folders in normal type and folders the download
  would create *in italics*, with a count of the files landing in each.

**Downloading**
- Copy or move, with a confirmation before a move.
- Conflicts resolved by unique suffix, skip, or overwrite.
- Optional verification: source and destination hashed and compared.
- Files are written to a `.sparkpart` temporary name and renamed on success, so an
  interrupted run never leaves a half-file that looks complete.
- Sidecars (XMP, THM, AAE, PP3, …) travel with the file they belong to and are renamed to
  match.
- Simultaneous backup to separate photo and video folders, mirroring the destination layout.
- Free-space check before starting; per-file progress; cancellable.
- Job Codes labelled per batch and usable inside folder and file names.

**Timeline**
- Files clustered by the gap between shots, adjustable from 5 minutes to 24 hours; a new day
  always starts a new group. Select groups to narrow the grid.

## Running it

```bash
dotnet run --project SparkPhotoDownloader2.csproj
```

The project and the solution sit in the same directory, so `dotnet` cannot guess which you
mean — name the target rather than relying on discovery:

```bash
dotnet build SparkPhotoDownloader.slnx
```

To produce a self-contained build:

```bash
dotnet publish SparkPhotoDownloader2.csproj -c Release -r win-x64 --self-contained
```

`ffmpeg` is optional; install it and put it on `PATH` for video frame previews.

## Building for macOS

`build/macos/build-macos.sh` publishes the app and assembles a proper `.app` bundle. It runs
on a Mac or cross-compiles from Windows or Linux.

```bash
./build/macos/build-macos.sh                     # arm64, Apple silicon
./build/macos/build-macos.sh --arch x64          # Intel
./build/macos/build-macos.sh --arch universal    # both, merged (macOS host only)
```

On a Mac it also produces a `.dmg`. Elsewhere it produces a `.tar.gz` — not a `.zip`,
because a zip written on Windows loses the executable bit and the app would not launch;
tar records the mode explicitly. Unpack it on the Mac with:

```bash
tar xzf SparkPhotoDownloader-macos-arm64.tar.gz
xattr -dr com.apple.quarantine "Spark Photo Downloader.app"
open "Spark Photo Downloader.app"
```

The `xattr` line is only needed for an unsigned build; without it Gatekeeper refuses to open
it. To sign and notarise instead, on a Mac with Xcode command line tools and a Developer ID:

```bash
./build/macos/build-macos.sh --sign "Developer ID Application: Your Name (TEAMID)" \
                             --notarize your-notary-keychain-profile
```

`build/macos/entitlements.plist` carries the JIT entitlements .NET needs under the hardened
runtime; without them a signed build is killed on launch. `build/macos/Info.plist` carries
the usage strings macOS shows when the program first asks to read a removable volume.

Signing and notarising need Apple's own tools, so they cannot be done from Windows.

Roughly 117 MB unpacked, 45 MB compressed — that is a self-contained build carrying the
whole .NET runtime plus Skia and HarfBuzz.

## Building for Linux

`build/linux/build-linux.sh` publishes the app and packages it. It runs on Linux, or
cross-compiles from Windows or macOS.

```bash
./build/linux/build-linux.sh                  # x64
./build/linux/build-linux.sh --arch arm64     # aarch64
./build/linux/build-linux.sh --arch both
```

Each architecture produces two things:

| Artifact | For |
| --- | --- |
| `spark-photo-downloader-1.0.0-linux-<arch>.tar.gz` | any distribution; unpack and run `install.sh` |
| `spark-photo-downloader_1.0.0_<debarch>.deb` | Debian, Ubuntu and derivatives |

An AppImage is built as well when `appimagetool` is on `PATH`.

**Portable tarball** — installs per-user without root, or system-wide with `-s`:

```bash
tar xzf spark-photo-downloader-1.0.0-linux-x64.tar.gz
./install.sh          # into ~/.local, no root needed
sudo ./install.sh -s  # into /opt and /usr, for everyone
```

`uninstall.sh` reverses it and leaves your history and settings alone.

**Package**:

```bash
sudo apt install ./spark-photo-downloader_1.0.0_amd64.deb
```

It puts the program in `/opt/spark-photo-downloader`, a launcher at
`/usr/bin/spark-photo-downloader`, and a desktop entry with hicolor icons, so it appears in
the applications menu.

The package is assembled without `dpkg-deb`, which is not available when cross-compiling
from Windows: a `.deb` is an `ar` archive of exactly three members, and the script writes
that container directly.

**Dependencies.** A self-contained build carries the .NET runtime but *not* ICU, which .NET
needs for culture-aware date formatting — the `.deb` declares it, and on other distributions
you want `libicu`, plus `fontconfig` and the usual X11 client libraries. `ffmpeg` remains
optional, for video frame previews.

## Where its data lives

`%LOCALAPPDATA%\SparkPhotoDownloader` on Windows, `~/.local/share/SparkPhotoDownloader`
elsewhere:

| File | Contents |
| --- | --- |
| `download-history.sqlite` | every downloaded file, and where it was written |
| `settings.json` | destinations, presets, Job Codes, view state |
| `thumbnails/` | cached previews |

Preferences has buttons to open that folder, clear the thumbnail cache, forget rows whose
copies are gone, and clear the history outright.

## Layout of the source

The project sits at the repository root, beside the solution.

```
SparkPhotoDownloader.slnx
SparkPhotoDownloader2.csproj
  Models/        file kinds, download states, naming presets, settings
  Services/      device and file scanning, metadata, thumbnails, naming,
                 the download engine, and the download history database
  ViewModels/    one per panel, plus the main window's orchestration
  Views/         the main window, the three card types, and four dialogs
  Styles/        palette and control styles
  Assets/        app.ico, app.icns and the hicolor PNG set
build/macos/     Info.plist, entitlements and the bundle build script
build/linux/     desktop entry, install/uninstall scripts and the package build script
artifacts/       build output; excluded from the project's item globs
```

## Platform notes

Device detection differs by platform, because the operating systems disagree about what
"removable" means. Windows reports it directly through `DriveInfo`. Unix does not — .NET
derives the drive type from the file system name, which never yields `Removable`, so a
memory card reads as a fixed disk. There, a volume counts as external when it is mounted
where the desktop mounts removable media: `/Volumes` on macOS, `/media`, `/run/media` and
`/mnt` on Linux. Kernel and system mounts are filtered out by path and by file system type.

## Licence and attribution

Released under the [GNU General Public License v3.0](LICENSE).

The design follows [Rapid Photo Downloader](https://damonlynch.net/rapid/) by Damon Lynch:
its three-column layout, its Timeline, its naming vocabulary and its rule for recognising a
file it has downloaded before. That work deserves the credit for the ideas.

No Rapid Photo Downloader code was used. This is an independent implementation in C#, written
against the program's public documentation and its described behaviour, and it is a separate
work rather than a derivative of that codebase.
