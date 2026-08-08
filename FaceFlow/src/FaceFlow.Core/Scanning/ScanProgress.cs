namespace FaceFlow.Core.Scanning;

public enum ScanPhase { Idle, Enumerating, Processing, Finishing, Cancelled, Completed, Failed }

public sealed class ScanProgress
{
    public ScanPhase Phase;
    public long FilesSeen;
    public long FilesQueued;      // new or changed -> actually need AI work
    public long FilesSkipped;     // unchanged since last scan
    public long Processed;
    public long Total;
    public long FacesFound;
    public long PeopleKnown;
    public long Failures;
    public double PhotosPerSecond;
    public TimeSpan Elapsed;
    public TimeSpan? Eta;
    public string CurrentFile = "";
    public string? Message;
    public bool Gpu;

    public double Percent => Total <= 0 ? 0 : Math.Clamp(Processed * 100.0 / Total, 0, 100);
}
