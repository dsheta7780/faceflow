# Licensing notes

## FaceFlow source code
Yours. Nothing in `src/` carries a third-party restriction.

## Runtime dependencies

| Component | Licence | Commercial use |
|---|---|---|
| .NET 8 / WPF | MIT | yes |
| Microsoft.ML.OnnxRuntime | MIT | yes |
| Microsoft.Data.Sqlite | MIT | yes |
| SQLite | public domain | yes |
| Windows Imaging Component (WIC) | part of Windows | yes |

None of the code dependencies are a problem.

## The models are the problem

FaceFlow ships **no model weights**. `get-models.ps1` downloads them from the
InsightFace project at run time, onto your machine.

The InsightFace pretrained packs (buffalo_l, buffalo_s, antelopev2 …) are
released for **non-commercial research purposes**. Redistributing them inside a
product you sell is not covered.

### If you want to sell FaceFlow

You need weights you are licensed to redistribute. Realistic paths:

1. **Buy a commercial licence** for the models you are already using. InsightFace
   has a commercial contact route; several detector/recogniser vendors sell
   per-seat or royalty terms.
2. **Swap in permissively licensed models.** Anything exported to ONNX works as
   long as the shapes match:
   - detector: SCRFD-style, 9 outputs (3 score + 3 bbox + 3 keypoint), 640×640 input
   - recogniser: 112×112 RGB input, single embedding output (512-d typical)
   `FaceEngine.FindModel` already looks for several filenames; add yours there.
3. **Train or fine-tune your own** on a dataset whose licence permits commercial
   use, then export to ONNX.
4. **Ship without weights** and have the customer supply or download them —
   legally the cleanest, but a worse first-run experience.

Also worth checking before you sell in any market: GDPR and equivalent
biometric-data rules. Face embeddings are biometric data in the EU, Illinois
(BIPA), Texas and elsewhere. FaceFlow keeps everything local and never uploads
anything, which is the right architecture for this, but "local only" should be a
documented, auditable claim rather than an assumption.

None of this is legal advice — I'm not a lawyer, and you'd want a real one before
you take money for it.
