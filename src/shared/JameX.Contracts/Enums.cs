namespace JameX.Contracts;

/// <summary>
/// Lifecycle of a video as it moves through the ingest pipeline of chapter 3:
/// client -> upload storage -> encoder -> blob storage -> CDN.
/// <para>
/// This enum crosses service boundaries, so its numeric values are part of the
/// wire contract. Append new members; never renumber existing ones.
/// </para>
/// </summary>
public enum VideoStatus
{
    /// <summary>A multipart upload is open but not yet completed.</summary>
    Uploading = 0,

    /// <summary>Raw file is in upload storage and an encode has been requested.</summary>
    Queued = 1,

    /// <summary>An encoder has claimed the job and is producing the ABR ladder.</summary>
    Transcoding = 2,

    /// <summary>Renditions are in blob storage and the master playlist is servable.</summary>
    Ready = 3,

    /// <summary>Encoding failed past the retry budget; the job sits in the DLQ.</summary>
    Failed = 4,

    /// <summary>Rejected before encoding — most often as a duplicate upload.</summary>
    Rejected = 5
}

public enum VideoPrivacy
{
    Private = 0,
    Unlisted = 1,
    Public = 2
}

/// <summary>
/// The doc models like/dislike as one API with a flag, and notes the flag also
/// accepts "none" to withdraw a reaction. Absence of a stored row is "none",
/// which is why this enum has no None member.
/// </summary>
public enum ReactionKind
{
    Like = 0,
    Dislike = 1
}

/// <summary>
/// Chapter 5 splits the catalogue by popularity to decide how far towards the
/// user content is pushed. Only the hot head is worth edge storage.
/// </summary>
public enum PopularityTier
{
    /// <summary>Origin storage servers, optimised for capacity over latency.</summary>
    Cold = 0,

    /// <summary>Colocation sites and origin flash servers.</summary>
    Warm = 1,

    /// <summary>Pushed out to CDN and ISP points of presence.</summary>
    Hot = 2
}
