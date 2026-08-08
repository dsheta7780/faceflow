namespace FaceFlow.Core.Faces;

public sealed class DetectedFace
{
    public float X, Y, W, H;          // bbox in source-image coordinates
    public float Score;
    public float[] Landmarks = new float[10];  // 5 points, x0,y0,x1,y1,...
    public float[]? Embedding;        // 512-d, L2-normalised
    public float Quality;             // 0..1 heuristic: detector score * size factor
}
