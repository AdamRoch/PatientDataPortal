using PatientDataPortal.Api.Seeding;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class ImagingSeedGeneratorTests
{
    [Fact]
    public void Plan_is_deterministic_and_stays_inside_the_storage_budget()
    {
        var first = ImagingSeedGenerator.DescribePlan();
        var second = ImagingSeedGenerator.DescribePlan();

        Assert.Equal(first, second);
        Assert.Equal(50, first.Patients);
        Assert.InRange(first.CompletedStudies, 50, 250);
        Assert.True(first.ScheduledStudies >= 5);
        Assert.True(first.CancelledStudies >= 4);
        Assert.True(first.Images > 0);
        Assert.True(first.CineClips > 0);
        Assert.True(first.HundredFrameClips >= 2);
        Assert.Equal(12, first.SignedReports);
        Assert.Equal(12, first.PreliminaryReports);
        Assert.True(first.StorageBytes < first.StorageBudgetBytes);
    }
}
