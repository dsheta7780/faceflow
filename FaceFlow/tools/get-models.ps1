<#
  Downloads the face models FaceFlow needs into the models\ folder.

  FaceFlow needs two ONNX files:
    * a detector  - SCRFD with keypoints (det_10g.onnx or det_500m.onnx)
    * a recogniser - ArcFace embeddings  (w600k_r50.onnx or w600k_mbf.onnx)

  LICENSING: the InsightFace pretrained models are published for non-commercial
  research use. That is fine for your own photo library. If you ever want to sell
  FaceFlow you must swap in models you are licensed to redistribute. See LICENSING.md.
#>

$ErrorActionPreference = "Stop"
$root      = Split-Path -Parent $PSScriptRoot
$modelsDir = Join-Path $root "models"
New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null

# buffalo_l = det_10g.onnx (accurate detector) + w600k_r50.onnx (512-d embeddings)
# buffalo_s = det_500m.onnx (fast detector)    + w600k_mbf.onnx (lighter, faster)
$pack = if ($args.Count -gt 0 -and $args[0] -eq "small") { "buffalo_s" } else { "buffalo_l" }
$url  = "https://github.com/deepinsight/insightface/releases/download/v0.7/$pack.zip"
$zip  = Join-Path $env:TEMP "$pack.zip"

Write-Host ""
Write-Host "  Downloading $pack ..." -ForegroundColor Cyan
Write-Host "  $url"
Write-Host ""

try {
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
} catch {
    Write-Host "  Download failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Download the pack manually and copy these two files into:" -ForegroundColor Yellow
    Write-Host "    $modelsDir"
    Write-Host "      det_10g.onnx    (or det_500m.onnx)"
    Write-Host "      w600k_r50.onnx  (or w600k_mbf.onnx)"
    exit 1
}

$staging = Join-Path $env:TEMP "faceflow_models"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $staging -Force

$wanted = @("det_10g.onnx","det_500m.onnx","det_2.5g.onnx","w600k_r50.onnx","w600k_mbf.onnx")
$copied = 0
Get-ChildItem $staging -Recurse -Filter *.onnx | ForEach-Object {
    if ($wanted -contains $_.Name) {
        Copy-Item $_.FullName (Join-Path $modelsDir $_.Name) -Force
        Write-Host ("  + " + $_.Name + "  (" + [math]::Round($_.Length/1MB,1) + " MB)") -ForegroundColor Green
        $copied++
    }
}

Remove-Item $zip -Force -ErrorAction SilentlyContinue
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

if ($copied -eq 0) {
    Write-Host "  No usable models found in the archive." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "  Done. $copied model file(s) in $modelsDir" -ForegroundColor Green
Write-Host ""
