using PatientDataPortal.Api.Imaging;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class ImageAccessServiceTests
{
    [Fact]
    public void Signed_storage_path_uses_the_owned_images_path_value()
    {
        var path = ImageAccessService.BuildSignPath("studies/study-id/images/image-id.jpg");

        Assert.Equal(
            "storage/v1/object/sign/study-assets/studies/study-id/images/image-id.jpg",
            path);
    }
}
