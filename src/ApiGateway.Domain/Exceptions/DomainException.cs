namespace ApiGateway.Domain.Exceptions;

public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
    public DomainException(string message, Exception inner) : base(message, inner) { }
}

public class PartnerConfigurationException : DomainException
{
    public PartnerConfigurationException(string message) : base(message) { }
}

public class SagaStateException : DomainException
{
    public SagaStateException(string message) : base(message) { }
}

public class SchemaVersionMismatchException : DomainException
{
    public string ExpectedVersion { get; }
    public string ActualVersion { get; }

    public SchemaVersionMismatchException(string expected, string actual)
        : base($"Schema version mismatch: partner expected '{expected}' but request carried '{actual}'")
    {
        ExpectedVersion = expected;
        ActualVersion = actual;
    }
}
