namespace ApiGateway.Application.Common;

public sealed class CorrelationContext : ICorrelationContextAccessor
{
    private static readonly AsyncLocal<CorrelationId?> _current = new();

    public Domain.ValueObjects.CorrelationId Current
    {
        get => _current.Value ?? Domain.ValueObjects.CorrelationId.New();
        set => _current.Value = value;
    }
}
