using ApiGateway.Application.Abstractions;
using ApiGateway.Domain.Entities;
using ApiGateway.Infrastructure.Audit;
using ApiGateway.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ApiGateway.Api.Middleware;

/// <summary>
/// Validates the inbound JSON body against the partner's effective
/// SchemaVersion. The partner may declare a specific version via the
/// X-Schema-Version header; otherwise we fall back to the latest
/// EffectiveDate &lt;= now.
///
/// On rejection, writes a sync audit record so the anomaly detector
/// can spot payload-shape drift (a partner whose requests suddenly
/// fail validation at high rate is a sign their client has drifted
/// from the contract).
/// </summary>
public sealed class SchemaValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ISchemaVersionRepository _schemaRepo;
    private readonly ISchemaValidator _validator;
    private readonly ISyncAuditWriter _audit;
    private readonly ILogger<SchemaValidationMiddleware> _logger;

    public SchemaValidationMiddleware(
        RequestDelegate next,
        ISchemaVersionRepository schemaRepo,
        ISchemaValidator validator,
        ISyncAuditWriter audit,
        ILogger<SchemaValidationMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _schemaRepo = schemaRepo ?? throw new ArgumentNullException(nameof(schemaRepo));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health")) {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // GET / DELETE don't carry a body — skip validation for those.
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var partnerId = context.Items["PartnerId"] as Guid?;
        if (partnerId is null)
        {
            // JwtAuth + RateLimit must have run first; if PartnerId isn't
            // populated, something is wrong upstream.
            context.Response.StatusCode = 500;
            return;
        }

        var declaredVersion = context.Request.Headers["X-Schema-Version"].ToString();
        if (string.IsNullOrWhiteSpace(declaredVersion)) declaredVersion = null;

        var schema = await _schemaRepo.GetEffectiveAsync(
            new Domain.ValueObjects.PartnerId(partnerId.Value), declaredVersion, context.RequestAborted)
            .ConfigureAwait(false);

        if (schema is null)
        {
            // No schema registered for this partner — fail closed. Ops
            // must publish a schema before the partner can route traffic.
            context.Response.StatusCode = 503;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://api.gateway/errors/no-schema",
                title = "No schema registered",
                status = 503,
                detail = "Partner has no published schema version"
            }).ConfigureAwait(false);
            return;
        }

        // Buffer the request body so we can validate and then re-expose
        // it to downstream middleware. The downstream controller reads
        // the body via the same Request.Body stream — EnableBuffering
        // gives us a seekable rewindable stream.
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        context.Request.Body.Position = 0;

        if (string.IsNullOrWhiteSpace(body))
        {
            // Empty body — accept only if the schema declares no required fields.
            // We let the validator decide.
            body = "{}";
        }

        var ok = await _validator.ValidateAsync(schema.JsonSchemaBlob, body, out var validationError, context.RequestAborted)
            .ConfigureAwait(false);

        if (!ok)
        {
            await _audit.WriteAsync(new AuditLogSecurity(
                eventType: SecurityEventTypes.SchemaValidationRejected,
                partnerId: partnerId,
                detail: $"version={schema.Version} error={validationError}"), context.RequestAborted).ConfigureAwait(false);

            context.Response.StatusCode = 422;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://api.gateway/errors/schema-validation",
                title = "Schema validation failed",
                status = 422,
                detail = validationError,
                schemaVersion = schema.Version
            }).ConfigureAwait(false);
            return;
        }

        // Stash the resolved schema version so downstream middleware and
        // the controller don't re-fetch it.
        context.Items["ResolvedSchemaVersion"] = schema.Version;

        await _next(context).ConfigureAwait(false);
    }
}
