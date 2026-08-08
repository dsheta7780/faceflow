using Microsoft.ML.OnnxRuntime;

namespace FaceFlow.Core.Faces;

public static class HardwareInfo
{
    public static IReadOnlyList<string> AvailableProviders()
    {
        try { return OrtEnv.Instance().GetAvailableProviders(); }
        catch { return Array.Empty<string>(); }
    }

    public static bool CudaAvailable()
        => AvailableProviders().Any(p => p.Contains("CUDA", StringComparison.OrdinalIgnoreCase));

    public static bool DirectMlAvailable()
        => AvailableProviders().Any(p => p.Contains("Dml", StringComparison.OrdinalIgnoreCase));

    public static string Describe()
    {
        var providers = AvailableProviders();
        var accel = CudaAvailable() ? "NVIDIA CUDA"
                  : DirectMlAvailable() ? "DirectML"
                  : "CPU only";
        return $"{Environment.ProcessorCount} logical cores · {accel}" +
               (providers.Count > 0 ? $" · providers: {string.Join(", ", providers)}" : "");
    }
}
