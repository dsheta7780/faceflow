using System.Numerics;

namespace FaceFlow.Core.Clustering;

public static class Vec
{
    /// <summary>Dot product of two equal-length float vectors, SIMD accelerated.</summary>
    public static float Dot(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        int w = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        int i = 0;
        for (; i <= n - w; i += w)
            acc += new Vector<float>(a, i) * new Vector<float>(b, i);
        float sum = Vector.Dot(acc, Vector<float>.One);
        for (; i < n; i++) sum += a[i] * b[i];
        return sum;
    }

    public static float Norm(float[] a) => MathF.Sqrt(Dot(a, a));

    /// <summary>Cosine similarity. Inputs need not be normalised.</summary>
    public static float Cosine(float[] a, float[] b)
    {
        float na = Norm(a), nb = Norm(b);
        if (na < 1e-9f || nb < 1e-9f) return 0f;
        return Dot(a, b) / (na * nb);
    }
}
