using KiriScope.Core.Evidence;

namespace KiriScope.Core.Tests;

public sealed class EvidenceStageTests
{
    [Fact]
    public void Stages_AreStrictlyOrderedFromIdentificationToUsableContent()
    {
        Assert.True(EvidenceStage.ContainerIdentified > EvidenceStage.Unidentified);
        Assert.True(EvidenceStage.IndexParsed > EvidenceStage.ContainerIdentified);
        Assert.True(EvidenceStage.ContentUsable > EvidenceStage.FormatValidated);
    }
}
