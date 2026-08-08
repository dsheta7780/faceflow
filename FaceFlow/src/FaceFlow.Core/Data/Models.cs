namespace FaceFlow.Core.Data;

public enum PhotoState { Pending = 0, Indexed = 1, Failed = 2, Unsupported = 3 }

public enum FaceStatus
{
    Assigned = 0,      // auto-assigned by the clusterer, above the match threshold
    Confirmed = 1,     // a human said yes
    NeedsReview = 2,   // borderline similarity, waiting in the Review screen
    Rejected = 3,      // a human said no; detached from the person
    Ignored = 4        // too small / too low quality to identify
}

// NOTE: these are properties, not fields, because WPF data binding cannot bind to fields.

public sealed class LibraryRow
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastScanAt { get; set; }
    public long PhotoCount { get; set; }
    public long PendingCount { get; set; }
    public string LastScanText => LastScanAt is { } d ? d.ToString("d MMM yyyy HH:mm") : "never scanned";
    public override string ToString() => Path;
}

public sealed class PhotoRow
{
    public long Id { get; set; }
    public long LibraryId { get; set; }
    public string Path { get; set; } = "";
    public long FileSize { get; set; }
    public long MTimeTicks { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int FaceCount { get; set; }
    public PhotoState State { get; set; }
    public string? Error { get; set; }
}

public sealed class FaceRow
{
    public long Id { get; set; }
    public long PhotoId { get; set; }
    public long? PersonId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    public float DetScore { get; set; }
    public float Quality { get; set; }
    public float Similarity { get; set; }
    public FaceStatus Status { get; set; }
    public string PhotoPath { get; set; } = "";
    public string? PersonName { get; set; }

    /// <summary>Only populated on the scan write path. UI reads never load embeddings.</summary>
    public float[]? EmbeddingRef { get; set; }
}

public sealed class PersonRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsNamed { get; set; }
    public long? CoverFaceId { get; set; }
    public int FaceCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class LibraryStats
{
    public long Photos { get; set; }
    public long Indexed { get; set; }
    public long Pending { get; set; }
    public long Failed { get; set; }
    public long Faces { get; set; }
    public long People { get; set; }
    public long NamedPeople { get; set; }
    public long NeedsReview { get; set; }
    public long NoFacePhotos { get; set; }
}
