using PatientDataPortal.Api.Seeding;
using System.Net;
using System.Text;
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
        Assert.Equal(1, first.CineClipsWithMissingFrames);
        Assert.Equal(12, first.SignedReports);
        Assert.Equal(12, first.PreliminaryReports);
        Assert.True(first.StorageBytes < first.StorageBudgetBytes);
    }

    [Fact]
    public async Task Existing_private_bucket_is_accepted_when_storage_wraps_duplicate_as_bad_request()
    {
        var requests = new List<(HttpMethod Method, string Path)>();
        using var http = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery.TrimStart('/')));
            return requests.Count switch
            {
                1 => JsonResponse(HttpStatusCode.BadRequest, """{"statusCode":"409","error":"Duplicate","message":"The resource already exists","code":"BucketAlreadyExists"}"""),
                2 => JsonResponse(HttpStatusCode.OK, """{"id":"study-assets","public":false}"""),
                _ => throw new InvalidOperationException("Unexpected Storage request."),
            };
        }))
        {
            BaseAddress = new Uri("https://project.supabase.co/"),
        };

        await ImagingSeedGenerator.EnsurePrivateBucketAsync(http, "study-assets", CancellationToken.None);

        Assert.Equal(
            [
                (HttpMethod.Post, "storage/v1/bucket"),
                (HttpMethod.Get, "storage/v1/bucket/study-assets"),
            ],
            requests);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
