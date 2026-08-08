# FaceFlow — Windows AI Photo Organiser

Native Windows desktop application. **C# / .NET 8 / WPF**, SQLite index,
ONNX Runtime inference. No Python runtime anywhere.

```
C#  →  WPF  →  SQLite  →  ONNX Runtime  →  Windows-native application
```

---

## First run — three steps

You need the **.NET 8 SDK** once, to compile. Get it from
<https://dotnet.microsoft.com/download/dotnet/8.0> (pick ".NET 8.0 SDK", x64).

```
1.  get-models.bat      downloads the two AI models into models\
2.  build.bat           compiles FaceFlow           (build-gpu.bat for NVIDIA)
3.  run.bat             starts the application
```

When you want a standalone folder you can copy to another PC, run `publish.bat`
and use `dist\FaceFlow.exe`.

---

## Your originals are never modified

This is enforced in one place — `src/FaceFlow.Core/Export/FolderExporter.cs` —
and nothing else in the codebase opens a source photo for writing.

| | |
|---|---|
| Source files opened | **read-only, always** |
| Resize / re-encode / convert | never on disk (only a temporary in-memory decode for detection) |
| Move / rename / delete | never |
| Folder creation | byte-for-byte `File.Copy` of the original |
| Export into your library folder | **blocked** — FaceFlow refuses the destination |
| Existing files at the destination | never overwritten; a numbered name is used |

"Reset index" and "Remove library" only clear FaceFlow's own database.

---

## Why it will not take ten minutes per 535 photos

The old Python MVP re-did all the work on every run. This one does not.

```
                       YOUR PHOTO LIBRARY
                              │
                     ┌────────▼────────┐
                     │  FAST FILE WALK │   size + last-write-time only
                     └────────┬────────┘
                              │
                    already indexed and unchanged?
                     ┌────────┴────────┐
                    YES               NO
                     │                 │
                   SKIP        ┌───────▼────────┐
                              │  N workers in  │
                              │   parallel:    │
                              │  decode ▸ detect
                              │  ▸ align ▸ embed
                              └───────┬────────┘
                                      │
                          ┌───────────▼───────────┐
                          │ one serialised writer │  SQLite + clustering
                          └───────────┬───────────┘
                                      ▼
                              PERSISTENT INDEX
```

The things that actually make it fast:

- **Fingerprint skip.** A file whose size and modified-time match the index is
  never decoded again. Ten million photos plus five hundred new ones is five
  hundred photos of AI work.
- **Decode at the size you need.** WIC decodes straight to the target resolution
  (`DecodePixelWidth`), so a 48-megapixel JPEG never fully materialises.
- **Parallel producers, single writer.** All the expensive work fans out across
  cores; only the cheap database write is serialised, so there is no lock
  contention and the centroids stay consistent.
- **Batched transactions and WAL mode**, with indexes on every column the UI filters on.
- **GPU when available.** ONNX Runtime CUDA provider, automatic CPU fallback if
  it fails to initialise — no configuration, no crash.
- **Resume.** Every photo is committed as it finishes. Pull the plug at
  6,824,192 of 10,000,000 and the next scan starts at 6,824,193.

---

## Screens

| Screen | What it does |
|---|---|
| **Dashboard** | Add folders, start/pause/stop scans, live throughput, live counters |
| **People** | Card gallery, search, open a person, rename, merge, split, create folder |
| **Needs Review** | Borderline matches one at a time — big photo, face crop, confidence |
| **No Faces** | Photos scanned successfully with zero faces, plus one-click folder export |
| **All Photos** | Everything indexed, searchable by path, opens in Explorer |
| **Settings** | Workers, decode size, GPU toggle, hardware report, cache and index tools |

### One photo, several people

A photo containing John, Sarah and David creates three face rows pointing at the
same photo row. Create a folder for each and the same original is copied into all
three — the source file is read three times and written zero times.

### Review is not cosmetic

Faces the clusterer is unsure about are attached to the suggested person **but
excluded from that person's face signature** until you press "Yes, this is them".
A wrong guess therefore cannot drift a cluster. Confirming recomputes the centroid;
rejecting recomputes it too.

---

## How recognition works

1. **SCRFD** finds faces and five landmarks per face.
2. A least-squares similarity transform warps each face onto the standard
   112×112 ArcFace template.
3. **ArcFace** turns the aligned face into a 512-number embedding, L2-normalised.
4. The embedding is compared by cosine similarity against the running mean of
   every known person.

| Cosine similarity | What happens |
|---|---|
| ≥ 0.42 | assigned automatically, absorbed into the person's mean |
| 0.30 – 0.42 | suggested, sent to **Needs Review**, *not* absorbed |
| < 0.30 | becomes a brand-new person |

Tune these in `src/FaceFlow.Core/Clustering/IncrementalClusterer.cs`. Raise
`MatchThreshold` for fewer wrong groupings and more clusters; lower it for the
opposite.

Naming a cluster matters: once "Person 14" becomes "John", every future scan
matches John automatically, and named clusters get a small tie-breaking bonus.

---

## Formats

JPG · PNG · BMP · GIF · TIFF · WEBP out of the box.
HEIC/HEIF and camera RAW (CR2, NEF, ARW, DNG…) work **if** the matching Windows
codec is installed — the HEIF Image Extensions and your camera maker's Raw
extension, both from the Microsoft Store. Files that fail to decode are logged
and skipped; one bad file never stops a scan.

---

## Where things live

```
%LOCALAPPDATA%\FaceFlow\
    faceflow.db      the index (photos, faces, people, embeddings)
    thumbs\          face thumbnails, sharded 256 ways
    logs\            one log file per day
    models\          fallback model location
```

---

## Layout

```
FaceFlow.sln
├─ src/FaceFlow.Core/          no UI, fully testable
│   ├─ Data/                   Db.cs, Repository.cs, Models.cs
│   ├─ Imaging/RgbImage.cs     WIC decode, bilinear sampling, thumbnail crops
│   ├─ Faces/                  FaceEngine.cs (SCRFD + ArcFace), GeometryUtil.cs
│   ├─ Clustering/             IncrementalClusterer.cs, SIMD Vec.cs
│   ├─ Scanning/               ScanEngine.cs, FileWalker.cs, ScanProgress.cs
│   └─ Export/FolderExporter.cs
└─ src/FaceFlow.App/           WPF
    ├─ Themes/Dark.xaml        palette, controls, scrollbars
    ├─ Views/AppViews.xaml     every screen, as DataTemplates
    ├─ ViewModels/
    └─ MainWindow.xaml
```

---

## Robustness

Not a promise of zero bugs — no honest engineer makes that promise about
non-trivial software. What is actually built in:

per-directory error isolation · corrupt and unreadable image skipping ·
unsupported-format handling · database transactions with rollback · WAL crash
safety · resumable scans · pause and cancel · long-path (>260 char) support ·
Unicode filenames · file-lock tolerant reads (`FileShare.ReadWrite`) ·
export collision handling · destination-inside-library refusal ·
global exception handlers that log and offer to keep running · daily log files.

---

## Licensing before you sell this

The InsightFace pretrained models (buffalo_l / buffalo_s) are published for
**non-commercial research use**. Using them on your own library is fine.
Shipping FaceFlow commercially with them is not. Read `LICENSING.md` before
distributing anything.
