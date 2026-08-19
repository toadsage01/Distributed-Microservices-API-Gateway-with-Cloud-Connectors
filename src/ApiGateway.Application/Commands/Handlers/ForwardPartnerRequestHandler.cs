using ApiGateway.Application.Abstractions;
using ApiGateway.Application.Contracts;
using ApiGateway.Application.Sagas;
using ApiGateway.Domain.ValueObjects;
using MediatR;

namespace ApiGateway.Application.Commands;

public sealed record ForwardPartnerRequestCommand(GatewayRequest Request) : IRequest<GatewayResponse>;

public sealed class ForwardPartnerRequestHandler : IRequestHandler<ForwardPartnerRequestCommand, GatewayResponse>
{
    private readonly IPartnerConfigLoader _partnerLoader;
    private readonly IIdempotencyKeyResolver _idempotency;
    private readonly SagaOrchestrator _saga;
    private readonly ICorrelationContextAccessor _correlation;

    public ForwardPartnerRequestHandler(
        IPartnerConfigLoader partnerLoader,
        IIdempotencyKeyResolver idempotency,
        SagaOrchestrator saga,
        ICorrelationContextAccessor correlation)
    {
        _partnerLoader = partnerLoader ?? throw new ArgumentNullException(nameof(partnerLoader));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));
        _saga = saga ?? throw new ArgumentNullException(nameof(saga));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
    }

    public async Task<GatewayResponse> Handle(ForwardPartnerRequestCommand request, CancellationToken ct)
    {
        var req = request.Request;

        // Resolve the partner config — failures here mean the partner
        // name on the URL doesn't map to a registered partner.
        var partner = await _partnerLoader.LoadAsync(req.PartnerName, ct).ConfigureAwait(false);
        if (partner is null)
        {
            return new GatewayResponse
            {
                StatusCode = 404,
                Body = $"Unknown partner: {req.PartnerName}",
                ContentType = "text/plain"
            };
        }

        // Idempotency-key fast path: if the partner supplied a key and
        // we've already served a 2xx for it, return the cached response
        // rather than re-running the saga. The request hash check means
        // a different request body with the same key returns null here
        // and the request proceeds to the saga (where it will eventually
        // fail because the key is taken — that's surfaced as a 409 in
        // the schema validation middleware, not in this handler).
        if (!string.IsNullOrWhiteSpace(req.IdempotencyKey) && req.Body is not null)
        {
            var requestHash = SHA256Hex(req.Body);
            var cached = await _idempotency.TryGetAsync(partner.PartnerId, req.IdempotencyKey, requestHash, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                return new GatewayResponse
                {
                    StatusCode = cached.StatusCode,
                    Body = cached.ResponsePayload,
                    ContentType = "application/json",
                    IsFromIdempotencyCache = true
                };
            }
        }

        // Set the correlation context so downstream middleware (audit
        // writer, logging) can pick it up without threading it through
        // every signature.
        _correlation.Current = req.CorrelationId;

        var sagaContext = new SagaContext
        {
            CorrelationId = req.CorrelationId,
            Request = req,
            Partner = partner
        };

        var response = await _saga.ExecuteAsync(sagaContext, ct).ConfigureAwait(false);

        // Cache 2xx responses against the idempotency key so a replay
        // returns the cached body. Non-2xx are deliberately not cached
        // — the partner is expected to retry with the same key.
        if (response.StatusCode is >= 200 and < 300
            && !string.IsNullOrWhiteSpace(req.IdempotencyKey)
            && req.Body is not null
            && !response.IsFromIdempotencyCache)
        {
            await _idempotency.StoreAsync(
                partner.PartnerId,
                req.IdempotencyKey,
                SHA256Hex(req.Body),
                response.StatusCode,
                response.Body,
                req.CorrelationId,
                ct).ConfigureAwait(false);
        }

        return response;
    }

    private static string SHA256Hex(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}
