using System.Windows.Media;
using FaceFlow.App.Mvvm;
using FaceFlow.Core.Data;

namespace FaceFlow.App.ViewModels;

/// <summary>A person card in the People gallery.</summary>
public sealed class PersonTile : ObservableObject
{
    private ImageSource? _thumb;
    public long Id { get; init; }
    public string Name { get; set; } = "";
    public int FaceCount { get; init; }
    public bool IsNamed { get; init; }
    public long? CoverFaceId { get; init; }
    public string CountText => FaceCount == 1 ? "1 photo" : $"{FaceCount:N0} photos";
    public ImageSource? Thumb { get => _thumb; set => Set(ref _thumb, value); }

    public void LoadThumb()
    {
        if (CoverFaceId is long id) Thumb = Thumbs.Face(id, 200);
    }

    public static PersonTile From(PersonRow p) => new()
    {
        Id = p.Id, Name = p.Name, FaceCount = p.FaceCount,
        IsNamed = p.IsNamed, CoverFaceId = p.CoverFaceId
    };
}

/// <summary>A photo card in a gallery.</summary>
public sealed class PhotoTile : ObservableObject
{
    private ImageSource? _thumb;
    private bool _selected;
    public long PhotoId { get; init; }
    public long? FaceId { get; init; }
    public string Path { get; init; } = "";
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public int FaceCount { get; init; }
    public float Similarity { get; init; }
    public FaceStatus Status { get; init; }
    public string ConfidenceText => Similarity <= 0 ? "" : $"{Similarity * 100:0}% match";
    public bool NeedsReview => Status == FaceStatus.NeedsReview;
    public ImageSource? Thumb { get => _thumb; set => Set(ref _thumb, value); }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

    public void LoadThumb(int width = 240)
    {
        Thumb = FaceId is long fid ? Thumbs.Face(fid, width) : null;
        Thumb ??= Thumbs.Load(Path, width);
    }
}
