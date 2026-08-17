using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Seeding;

/// <summary>Creates the small, explicitly opt-in set of pre-confirmed demo logins.</summary>
public sealed class DemoAccountSeedGenerator
{
    public const string PasswordEnvironmentVariable = "DEMO_SEED_PASSWORD";
    public const string LinkedPatientReference = "SYN-0001";

    public static DemoAccountSeedPlan DescribePlan() => DemoAccountSeedPlan.Create();

    public async Task<DemoAccountSeedSummary> SeedAsync(CancellationToken cancellationToken = default)
    {
        var databaseUrl = RequiredEnvironment("DATABASE_URL");
        var supabaseUrl = RequiredEnvironment("SUPABASE_URL").TrimEnd('/');
        var serviceKey = RequiredEnvironment("SUPABASE_SERVICE_KEY");
        var password = RequiredEnvironment(PasswordEnvironmentVariable);
        if (password.Length < 12) throw new InvalidOperationException($"{PasswordEnvironmentVariable} must be at least 12 characters.");

        var plan = DescribePlan();
        using var http = new HttpClient { BaseAddress = new Uri(supabaseUrl + "/") };
        http.DefaultRequestHeaders.Add("apikey", serviceKey);
        http.DefaultRequestHeaders.Authorization = new("Bearer", serviceKey);

        var users = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in plan.Accounts)
            users[account.Email] = await EnsureAuthUserAsync(http, account, password, cancellationToken);

        await using var dataSource = NpgsqlDataSource.Create(DatabaseConnectionString.Normalize(databaseUrl));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var account in plan.Accounts)
        {
            var userId = users[account.Email];
            await UpsertProfileAsync(connection, transaction, userId, account, cancellationToken);
            if (account.Role == "provider") await UpsertProviderAsync(connection, transaction, userId, cancellationToken);
            if (account.ClaimsLinkedPatient) await ClaimLinkedPatientAsync(connection, transaction, userId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        return new DemoAccountSeedSummary(plan.Accounts.Count, plan.Accounts.Count(account => account.Role == "provider"), plan.Accounts.Count(account => account.Role == "patient"));
    }

    private static async Task<Guid> EnsureAuthUserAsync(HttpClient http, DemoSeedAccount account, string password, CancellationToken cancellationToken)
    {
        var existing = await FindUserAsync(http, account.Email, cancellationToken);
        if (existing is { } userId)
        {
            using var update = new HttpRequestMessage(HttpMethod.Put, $"auth/v1/admin/users/{userId}")
            {
                Content = JsonContent.Create(new { email_confirm = true, user_metadata = new { demo_seed = true } }),
            };
            using var response = await http.SendAsync(update, cancellationToken);
            response.EnsureSuccessStatusCode();
            return userId;
        }

        using var create = new HttpRequestMessage(HttpMethod.Post, "auth/v1/admin/users")
        {
            Content = JsonContent.Create(CreateAuthUserRequest(account, password)),
        };
        using var createResponse = await http.SendAsync(create, cancellationToken);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<SupabaseUser>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException($"Supabase Auth did not return the created user for {account.Email}.");
        return ParseUserId(created, account.Email);
    }

    private static async Task<Guid?> FindUserAsync(HttpClient http, string email, CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        for (var page = 1; ; page++)
        {
            using var response = await http.GetAsync($"auth/v1/admin/users?page={page}&per_page={pageSize}", cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<SupabaseUsers>(cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Supabase Auth did not return its user list.");
            var user = result.Users.FirstOrDefault(candidate => string.Equals(candidate.Email, email, StringComparison.OrdinalIgnoreCase));
            if (user is not null) return ParseUserId(user, email);
            if (result.Users.Count < pageSize) return null;
        }
    }

    private static Guid ParseUserId(SupabaseUser user, string email) => Guid.TryParse(user.Id, out var id)
        ? id
        : throw new InvalidOperationException($"Supabase Auth returned an invalid user id for {email}.");

    internal static DemoAuthUserCreateRequest CreateAuthUserRequest(DemoSeedAccount account, string password) =>
        new(account.Email, password, true, new DemoSeedUserMetadata(true));

    private static Task UpsertProfileAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, DemoSeedAccount account, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, "INSERT INTO user_profiles (user_id, role, display_name, tz) VALUES (@user_id, @role, @display_name, @tz) ON CONFLICT (user_id) DO UPDATE SET role = EXCLUDED.role, display_name = EXCLUDED.display_name, tz = EXCLUDED.tz", cancellationToken,
            ("user_id", userId), ("role", account.Role), ("display_name", account.DisplayName), ("tz", "America/Chicago"));

    private static Task UpsertProviderAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, "INSERT INTO providers (id, user_id, tz, slot_length_min) VALUES (@id, @user_id, @tz, @slot_length_min) ON CONFLICT (user_id) DO UPDATE SET tz = EXCLUDED.tz, slot_length_min = EXCLUDED.slot_length_min", cancellationToken,
            ("id", DeterministicGuid("demo-provider")), ("user_id", userId), ("tz", "America/Chicago"), ("slot_length_min", 30));

    private static async Task ClaimLinkedPatientAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, CancellationToken cancellationToken)
    {
        const string claim = "UPDATE patient_records SET claimed_by = @user_id, claimed_at = now() WHERE patient_ref = @reference AND claimed_by IS NULL RETURNING id";
        await using var command = new NpgsqlCommand(claim, connection, transaction);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("reference", LinkedPatientReference);
        var recordId = await command.ExecuteScalarAsync(cancellationToken);
        if (recordId is Guid id)
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO patient_claim_events (id, patient_record_id, actor_user_id, event_type) VALUES (@id, @record_id, @actor_id, 'claim')", cancellationToken,
                ("id", Guid.NewGuid()), ("record_id", id), ("actor_id", userId));
            await ExecuteAsync(connection, transaction, "INSERT INTO audit_log (id, actor_reference, actor_role, action, target_type, target_reference, result) VALUES (@id, @actor, 'patient', 'patient_claim', 'patient_record', @target, 'allowed')", cancellationToken,
                ("id", Guid.NewGuid()), ("actor", userId.ToString()), ("target", id.ToString()));
            return;
        }

        await using var ownership = new NpgsqlCommand("SELECT claimed_by FROM patient_records WHERE patient_ref = @reference", connection, transaction);
        ownership.Parameters.AddWithValue("reference", LinkedPatientReference);
        var existingOwner = await ownership.ExecuteScalarAsync(cancellationToken);
        if (existingOwner is null) throw new InvalidOperationException($"Run --seed-imaging before --seed-demo-accounts: {LinkedPatientReference} was not found.");
        if (existingOwner is Guid owner && owner == userId) return;
        throw new InvalidOperationException($"{LinkedPatientReference} is already claimed by a different account; refusing to overwrite the claim.");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Guid DeterministicGuid(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("PTDP-16:" + value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string RequiredEnvironment(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"{name} must be set before running --seed-demo-accounts.");

    private sealed record SupabaseUser(string? Id, string? Email);
    private sealed record SupabaseUsers(List<SupabaseUser> Users);
}

internal sealed record DemoAuthUserCreateRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("email_confirm")] bool EmailConfirm,
    [property: JsonPropertyName("user_metadata")] DemoSeedUserMetadata UserMetadata);
internal sealed record DemoSeedUserMetadata([property: JsonPropertyName("demo_seed")] bool DemoSeed);

public sealed record DemoSeedAccount(string Email, string Role, string DisplayName, bool ClaimsLinkedPatient);

public sealed record DemoAccountSeedPlan(IReadOnlyList<DemoSeedAccount> Accounts)
{
    public static DemoAccountSeedPlan Create() => new([
        new DemoSeedAccount("demo-admin@patient-data-portal.test", "admin", "Demo Admin", false),
        new DemoSeedAccount("demo-provider@patient-data-portal.test", "provider", "Demo Provider", false),
        new DemoSeedAccount("demo-patient@patient-data-portal.test", "patient", "Demo Patient", true),
        new DemoSeedAccount("demo-unlinked@patient-data-portal.test", "patient", "Demo Unlinked Patient", false),
    ]);
}

public sealed record DemoAccountSeedSummary(int Accounts, int Providers, int Patients)
{
    public string ToLogLine() => $"demo-account-seed accounts={Accounts} providers={Providers} patients={Patients} linked_patient_ref={DemoAccountSeedGenerator.LinkedPatientReference}";
}
