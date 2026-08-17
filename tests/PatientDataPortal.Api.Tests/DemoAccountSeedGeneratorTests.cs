using PatientDataPortal.Api.Seeding;
using System.Text.Json;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class DemoAccountSeedGeneratorTests
{
    [Fact]
    public void Plan_has_the_required_roles_and_exactly_one_preverified_patient()
    {
        var plan = DemoAccountSeedGenerator.DescribePlan();

        Assert.Equal(4, plan.Accounts.Count);
        Assert.Equal(new[] { "admin", "provider", "patient", "patient" }, plan.Accounts.Select(account => account.Role));
        var linkedPatient = Assert.Single(plan.Accounts, account => account.ClaimsLinkedPatient);
        Assert.Equal("patient", linkedPatient.Role);
        Assert.Contains(plan.Accounts, account => account.Email == "demo-unlinked@patient-data-portal.test" && !account.ClaimsLinkedPatient);
    }

    [Fact]
    public void Plan_is_deterministic_and_does_not_embed_a_password()
    {
        Assert.Equal(
            DemoAccountSeedGenerator.DescribePlan().Accounts,
            DemoAccountSeedGenerator.DescribePlan().Accounts);
        Assert.Equal("DEMO_SEED_PASSWORD", DemoAccountSeedGenerator.PasswordEnvironmentVariable);
        Assert.Equal("SYN-0001", DemoAccountSeedGenerator.LinkedPatientReference);
    }

    [Fact]
    public void Auth_admin_request_marks_only_the_demo_seed_account_as_confirmed()
    {
        var account = DemoAccountSeedGenerator.DescribePlan().Accounts[0];

        var request = DemoAccountSeedGenerator.CreateAuthUserRequest(account, "a-test-password");

        Assert.Equal(account.Email, request.Email);
        Assert.Equal("a-test-password", request.Password);
        Assert.True(request.EmailConfirm);
        Assert.True(request.UserMetadata.DemoSeed);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request));
        Assert.True(json.RootElement.GetProperty("email_confirm").GetBoolean());
        Assert.True(json.RootElement.GetProperty("user_metadata").GetProperty("demo_seed").GetBoolean());
    }
}
