using FaceFlow.Core.Data;

namespace FaceFlow.Core.Clustering;

public sealed class ClusterOptions
{
    /// <summary>At or above this cosine similarity a face is auto-assigned to an existing person.</summary>
    public float MatchThreshold = 0.42f;

    /// <summary>Between ReviewThreshold and MatchThreshold the face lands in the Review queue.</summary>
    public float ReviewThreshold = 0.30f;

    /// <summary>Faces below this quality are stored but never used to form or grow a cluster.</summary>
    public float MinQuality = 0.30f;

    /// <summary>Confirmed clusters are weighted slightly higher so named people win ties.</summary>
    public float NamedBonus = 0.02f;
}

/// <summary>
/// Online centroid clustering. Every new embedding is compared against the running mean of
/// each existing person, so naming a cluster once makes all future photos of that person
/// land in it automatically. Deliberately single-threaded: it is the scan pipeline's
/// serialised consumer, which keeps centroid updates consistent without locking.
/// </summary>
public sealed class IncrementalClusterer
{
    private sealed class Centroid
    {
        public long PersonId;
        public float[] Sum = Array.Empty<float>();
        public int N;
        public float[] Unit = Array.Empty<float>();
        public bool IsNamed;
        public void Recompute()
        {
            var u = new float[Sum.Length];
            float norm = Vec.Norm(Sum);
            if (norm < 1e-9f) norm = 1e-9f;
            for (int i = 0; i < Sum.Length; i++) u[i] = Sum[i] / norm;
            Unit = u;
        }
    }

    private readonly Repository _repo;
    private readonly ClusterOptions _opt;
    private readonly List<Centroid> _centroids = new();
    private int _autoNameCounter;

    public IncrementalClusterer(Repository repo, ClusterOptions? options = null)
    {
        _repo = repo;
        _opt = options ?? new ClusterOptions();
        Reload();
    }

    public int PersonCount => _centroids.Count;

    public void Reload()
    {
        _centroids.Clear();
        var named = _repo.GetPeople().ToDictionary(p => p.Id, p => p.IsNamed);
        foreach (var (id, sum, n) in _repo.LoadCentroids())
        {
            var c = new Centroid { PersonId = id, Sum = sum, N = n, IsNamed = named.TryGetValue(id, out var v) && v };
            c.Recompute();
            _centroids.Add(c);
        }
        _autoNameCounter = _repo.GetPeople().Count;
    }

    public sealed record Assignment(long? PersonId, float Similarity, FaceStatus Status, bool CreatedNewPerson);

    /// <summary>
    /// Decide where a face belongs. Does NOT create the person row — the caller does that
    /// after the face id is known, so the cover thumbnail can point at a real face.
    /// </summary>
    public Assignment Classify(float[] embedding, float quality)
    {
        if (quality < _opt.MinQuality)
            return new Assignment(null, 0f, FaceStatus.Ignored, false);

        float best = -1f;
        Centroid? bestC = null;

        foreach (var c in _centroids)
        {
            if (c.Unit.Length != embedding.Length) continue;
            float s = Vec.Dot(c.Unit, embedding) + (c.IsNamed ? _opt.NamedBonus : 0f);
            if (s > best) { best = s; bestC = c; }
        }

        if (bestC is not null && best >= _opt.MatchThreshold)
            return new Assignment(bestC.PersonId, best, FaceStatus.Assigned, false);

        if (bestC is not null && best >= _opt.ReviewThreshold)
            return new Assignment(bestC.PersonId, best, FaceStatus.NeedsReview, false);

        return new Assignment(null, best < 0 ? 0 : best, FaceStatus.Assigned, true);
    }

    /// <summary>Grow an existing cluster with a newly confirmed/assigned face.</summary>
    public void Absorb(long personId, float[] embedding)
    {
        var c = _centroids.FirstOrDefault(x => x.PersonId == personId);
        if (c is null) return;
        if (c.Sum.Length != embedding.Length) return;
        for (int i = 0; i < embedding.Length; i++) c.Sum[i] += embedding[i];
        c.N++;
        c.Recompute();
        _repo.UpdatePersonCentroid(personId, c.Sum, c.N, c.N, null);
    }

    /// <summary>Create a brand-new unnamed cluster seeded by this face.</summary>
    public long CreateCluster(float[] embedding, long coverFaceId)
    {
        _autoNameCounter++;
        var name = $"Person {_autoNameCounter}";
        var sum = (float[])embedding.Clone();
        var id = _repo.CreatePerson(name, isNamed: false, sum, 1, coverFaceId);
        var c = new Centroid { PersonId = id, Sum = sum, N = 1, IsNamed = false };
        c.Recompute();
        _centroids.Add(c);
        return id;
    }
}
