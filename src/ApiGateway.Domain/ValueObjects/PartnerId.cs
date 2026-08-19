namespace ApiGateway.Domain.ValueObjects;

public readonly struct PartnerId : IEquatable<PartnerId>
{
    public Guid Value { get; }

    public PartnerId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("PartnerId cannot be empty", nameof(value));
        Value = value;
    }

    public static PartnerId New() => new(Guid.NewGuid());
    public static PartnerId Parse(string s) => new(Guid.Parse(s));

    public bool Equals(PartnerId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is PartnerId p && Equals(p);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value.ToString("D");

    public static bool operator ==(PartnerId a, PartnerId b) => a.Equals(b);
    public static bool operator !=(PartnerId a, PartnerId b) => !a.Equals(b);

    public static implicit operator Guid(PartnerId p) => p.Value;
    public static implicit operator PartnerId(Guid g) => new(g);
}
