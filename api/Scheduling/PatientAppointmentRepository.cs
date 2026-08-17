using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Scheduling;

public sealed record PatientAppointment(Guid Id, Guid ProviderId, Guid ServiceId, string ProviderName, string ServiceName, DateTimeOffset StartsAt, string Status);
public sealed record PatientAppointments(IReadOnlyList<PatientAppointment> Upcoming, IReadOnlyList<PatientAppointment> Past);

public interface IPatientAppointmentRepository
{
    Task<PatientAppointments> ListForPatientAsync(Guid patientUserId, CancellationToken cancellationToken);
}

public sealed class PatientAppointmentRepository(IOptions<DatabaseOptions> databaseOptions, IClock clock) : IPatientAppointmentRepository
{
    public async Task<PatientAppointments> ListForPatientAsync(Guid patientUserId, CancellationToken cancellationToken)
    {
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("Database is not configured.");

        await using var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(connectionString));
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT a.id, a.provider_id, a.service_id, profile.display_name, s.name, a.start_at, a.status
            FROM appointments a
            JOIN providers p ON p.id = a.provider_id
            JOIN user_profiles profile ON profile.user_id = p.user_id
            JOIN services s ON s.id = a.service_id
            WHERE a.patient_user_id = $1
            ORDER BY a.start_at DESC
            """;

        var upcoming = new List<PatientAppointment>();
        var past = new List<PatientAppointment>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(patientUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var now = clock.GetCurrentInstant().ToDateTimeOffset();
        while (await reader.ReadAsync(cancellationToken))
        {
            var appointment = new PatientAppointment(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6));
            (appointment.StartsAt >= now ? upcoming : past).Add(appointment);
        }

        upcoming.Sort((left, right) => left.StartsAt.CompareTo(right.StartsAt));
        return new PatientAppointments(upcoming, past);
    }
}
