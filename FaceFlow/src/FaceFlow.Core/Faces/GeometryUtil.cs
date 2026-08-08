namespace FaceFlow.Core.Faces;

internal static class GeometryUtil
{
    /// <summary>ArcFace 112x112 reference landmarks (left eye, right eye, nose, left mouth, right mouth).</summary>
    public static readonly float[,] ArcFaceDst =
    {
        { 38.2946f, 51.6963f },
        { 73.5318f, 51.5014f },
        { 56.0252f, 71.7366f },
        { 41.5493f, 92.3655f },
        { 70.7299f, 92.2041f }
    };

    /// <summary>
    /// Least-squares 2D similarity transform (uniform scale + rotation + translation)
    /// mapping src -> dst. Returns [a, b, tx, ty] where
    ///   dst.x = a*src.x - b*src.y + tx
    ///   dst.y = b*src.x + a*src.y + ty
    /// This is the closed-form solution; no SVD needed for the 2D similarity case.
    /// </summary>
    public static float[] SimilarityTransform(float[] srcXy, float[,] dst, int n)
    {
        float sxm = 0, sym = 0, dxm = 0, dym = 0;
        for (int i = 0; i < n; i++)
        {
            sxm += srcXy[i * 2]; sym += srcXy[i * 2 + 1];
            dxm += dst[i, 0];    dym += dst[i, 1];
        }
        sxm /= n; sym /= n; dxm /= n; dym /= n;

        float num = 0, cross = 0, den = 0;
        for (int i = 0; i < n; i++)
        {
            float px = srcXy[i * 2] - sxm, py = srcXy[i * 2 + 1] - sym;
            float qx = dst[i, 0] - dxm,    qy = dst[i, 1] - dym;
            num   += px * qx + py * qy;
            cross += px * qy - py * qx;
            den   += px * px + py * py;
        }
        if (den < 1e-8f) den = 1e-8f;

        float a = num / den, b = cross / den;
        float tx = dxm - (a * sxm - b * sym);
        float ty = dym - (b * sxm + a * sym);
        return new[] { a, b, tx, ty };
    }

    /// <summary>Invert the similarity transform so we can map destination pixels back to source.</summary>
    public static float[] Invert(float[] m)
    {
        float a = m[0], b = m[1], tx = m[2], ty = m[3];
        float det = a * a + b * b;
        if (det < 1e-12f) det = 1e-12f;
        float ia = a / det, ib = -b / det;
        float itx = -(ia * tx - ib * ty);
        float ity = -(ib * tx + ia * ty);
        return new[] { ia, ib, itx, ity };
    }

    public static float IoU(DetectedFace a, DetectedFace b)
    {
        float x1 = MathF.Max(a.X, b.X), y1 = MathF.Max(a.Y, b.Y);
        float x2 = MathF.Min(a.X + a.W, b.X + b.W), y2 = MathF.Min(a.Y + a.H, b.Y + b.H);
        float iw = MathF.Max(0, x2 - x1), ih = MathF.Max(0, y2 - y1);
        float inter = iw * ih;
        float uni = a.W * a.H + b.W * b.H - inter;
        return uni <= 0 ? 0 : inter / uni;
    }

    public static List<DetectedFace> Nms(List<DetectedFace> boxes, float thresh)
    {
        boxes.Sort((p, q) => q.Score.CompareTo(p.Score));
        var keep = new List<DetectedFace>(boxes.Count);
        var dead = new bool[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
        {
            if (dead[i]) continue;
            keep.Add(boxes[i]);
            for (int j = i + 1; j < boxes.Count; j++)
                if (!dead[j] && IoU(boxes[i], boxes[j]) > thresh) dead[j] = true;
        }
        return keep;
    }
}
