namespace PatientDataPortal.Api.Health;

public sealed record DependencyHealth(string Status, long LatencyMs);

public sealed record HealthResponse(
    string Status,
    DependencyHealth App,
    DependencyHealth Database,
    DependencyHealth Storage);
