# Get a compiled FaceFlow.exe without installing anything

You don't need Visual Studio, the .NET SDK, or a compiler on your laptop.
GitHub will build FaceFlow for you on a real Windows machine, run the compile,
and hand you a finished folder to download. It's free for public repositories.

## Steps (about five minutes, once)

**1. Make a GitHub account** at <https://github.com> if you don't have one.

**2. Create an empty repository.**
   Click **+** (top right) → **New repository** → name it `faceflow` →
   **Create repository**. Leave everything else alone.

**3. Upload this project.**
   On the new empty repo page click **uploading an existing file**.
   Open the extracted `FaceFlow` folder on your PC, select **everything inside
   it**, and drag it all into the browser window.

   Important: drag the *contents* of the `FaceFlow` folder — you should see
   `FaceFlow.sln`, `src`, `tools`, `.github` and the rest at the top level of
   the repo, not a single folder called `FaceFlow`.

   Then click **Commit changes**.

**4. Watch it build.**
   Click the **Actions** tab. A run called *Build FaceFlow* starts by itself.
   Give it three to five minutes.

**5a. Green tick → download your app.**
   Open the run, scroll to **Artifacts**, download **FaceFlow-windows-x64**.
   Unzip it and double-click `FaceFlow.exe`. That's a fully compiled,
   self-contained application — the machine running it needs no .NET installed.

**5b. Red cross → send me the errors.**
   Open the run, click the red step, and copy the red error lines.
   Paste them to me and I'll fix them and give you an updated ZIP.
   Repeat until green.

## Why this is worth doing

Every push rebuilds automatically. When I send you a fix, you re-upload the
changed files and GitHub compiles it again — you never install a compiler, and
the errors come back in a form I can act on immediately.

## The alternative, if you'd rather not use GitHub

Install the .NET 8 SDK once from
<https://dotnet.microsoft.com/download/dotnet/8.0> and run `build.bat`.
Same result, one 200 MB install.
