namespace KiriScope.Core.Evidence;

/// <summary>
/// Describes the highest verified state for an archive or resource.
/// Stages are intentionally ordered and must not be conflated with a single success flag.
/// </summary>
public enum EvidenceStage
{
    Unidentified = 0,
    ContainerIdentified = 1,
    IndexParsed = 2,
    EntryLocated = 3,
    RawDataExtracted = 4,
    ContentFilterApplied = 5,
    FormatValidated = 6,
    ContentUsable = 7,
}
