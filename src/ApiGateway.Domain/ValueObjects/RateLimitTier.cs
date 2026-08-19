namespace ApiGateway.Domain.ValueObjects;

public enum RateLimitTier
{
    Standard,
    Premium,
    Internal,
    Legacy
}

public static class RateLimitTierExtensions
{
    public static int RequestsPerMinute(this RateLimitTier tier) => tier switch
    {
        RateLimitTier.Standard => 120,
        RateLimitTier.Premium => 600,
        RateLimitTier.Internal => 2_000,
        RateLimitTier.Legacy => 30,
        _ => throw new ArgumentOutOfRangeException(nameof(tier))
    };

    public static int BurstCapacity(this RateLimitTier tier) => tier switch
    {
        RateLimitTier.Standard => 60,
        RateLimitTier.Premium => 200,
        RateLimitTier.Internal => 500,
        RateLimitTier.Legacy => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(tier))
    };
}
