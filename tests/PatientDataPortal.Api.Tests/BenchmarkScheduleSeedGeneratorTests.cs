using PatientDataPortal.Api.Seeding;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class BenchmarkScheduleSeedGeneratorTests
{
    [Fact]
    public void Plan_has_the_declared_deterministic_benchmark_shape()
    {
        var first = BenchmarkScheduleSeedGenerator.DescribePlan();
        var second = BenchmarkScheduleSeedGenerator.DescribePlan();

        Assert.Equal(first, second);
        Assert.Equal(10, first.Providers);
        Assert.Equal(16_000, first.Slots);
        Assert.Equal(new DateOnly(2030, 1, 7), first.FirstBusinessDay);
        Assert.Equal(100, first.BusinessDays);
        Assert.Equal(16, first.SlotsPerBusinessDay);
    }
}
