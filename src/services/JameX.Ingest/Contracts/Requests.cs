namespace JameX.Ingest.Contracts;

/// <summary>
/// The browser reporting that one part landed in S3.
/// <para>
/// This call carries no bytes — just the receipt. It exists so Ingest can
/// answer "which parts do I still need?" after a dropped connection, which is
/// the whole basis of resumability.
/// </para>
/// </summary>
public sealed record ReportPartRequest(string ETag);
