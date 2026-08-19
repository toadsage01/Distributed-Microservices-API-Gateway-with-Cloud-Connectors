namespace ApiGateway.Domain.ValueObjects;

/// <summary>
/// Correlates all work for a single client request across the saga pipeline,
/// audit logs, and downstream upstream calls. Persisted on the SagaExecutionLog
/// so a process restart can pick up the in-flight unit of work.
/// </summary>
public readonly struct CorrelationId : IEquatable<CorrelationId>
{
    public Guid Value { get; }

    public CorrelationId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("CorrelationId cannot be empty", nameof(value));
        Value = value;
    }

    public static CorrelationId New() => new(Guid.NewGuid());
    public static CorrelationId Parse(string s) => new(Guid.Parse(s));

    public bool Equals(CorrelationId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is CorrelationId c && Equals(c);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value.ToString("D");

    public static bool operator ==(CorrelationId a, CorrelationId b) => a.Equals(b);
    public static bool operator !=(CorrelationId a, CorrelationId b) => !a.Equals(b);

    public static implicit operator Guid(CorrelationId c) => c.Value;
    public static implicit operator CorrelationId(Guid g) => new(g);
}
