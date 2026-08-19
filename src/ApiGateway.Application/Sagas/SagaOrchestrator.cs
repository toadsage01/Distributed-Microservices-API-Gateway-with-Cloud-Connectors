using ApiGateway.Application.Contracts;
using ApiGateway.Domain.Entities;
using ApiGateway.Domain.ValueObjects;
using ApiGateway.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Logging;

namespace ApiGateway.Application.Sagas;

/// <summary>
/// Orchestrates a saga: persists each step before running it, walks
/// back through completed steps to invoke compensation on failure, and
/// is restart-safe via SagaExecutionLog state machine.
///
/// The orchestrator is in-process — not a separate coordinator service
/// — because the gateway brief explicitly favours a lightweight footprint.
/// Crash recovery is delegated to a BackgroundService that scans for
/// stale SagaExecutionLog rows and re-invokes compensation via this
/// same orchestrator.
/// </summary>
public sealed class SagaOrchestrator
{
    private readonly ISagaExecutionLogRepository _logRepo;
    private readonly IEnumerable<ISagaStep> _steps;
    private readonly ILogger<SagaOrchestrator> _logger;

    public SagaOrchestrator(
        ISagaExecutionLogRepository logRepo,
        IEnumerable<ISagaStep> steps,
        ILogger<SagaOrchestrator> logger)
    {
        _logRepo = logRepo ?? throw new ArgumentNullException(nameof(logRepo));
        _steps = steps ?? throw new ArgumentNullException(nameof(steps));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GatewayResponse> ExecuteAsync(SagaContext context, CancellationToken ct = default)
    {
        var orderedSteps = _steps.OrderBy(s => s.Order).ToList();
        var executed = new List<(ISagaStep Step, SagaExecutionLog Log)>();

        foreach (var step in orderedSteps)
        {
            var log = SagaExecutionLog.StartNew(
                context.CorrelationId,
                step.Name,
                step.Order,
                payloadSnapshot: null);

            await _logRepo.InsertAsync(log, ct).ConfigureAwait(false);
            log.MarkRunning();
            await _logRepo.UpdateStateAsync(log.Id, SagaState.Running, log.PayloadSnapshot, ct).ConfigureAwait(false);

            try
            {
                var result = await step.ExecuteAsync(context, ct).ConfigureAwait(false);
                if (!result.Success)
                {
                    log.MarkFailed();
                    await _logRepo.UpdateStateAsync(log.Id, SagaState.Failed, result.PayloadSnapshot, ct).ConfigureAwait(false);

                    _logger.LogWarning("Saga step {Step} failed for correlation {Corr}: {Reason}",
                        step.Name, context.CorrelationId, result.FailureReason);

                    await CompensateAsync(context, executed, ct).ConfigureAwait(false);

                    return new GatewayResponse
                    {
                        StatusCode = result.UpstreamResult?.StatusCode is int sc && sc >= 500 ? sc : 502,
                        Body = result.FailureReason ?? "Saga step failed",
                        ContentType = "text/plain"
                    };
                }

                log.MarkCompleted(result.PayloadSnapshot);
                await _logRepo.UpdateStateAsync(log.Id, SagaState.Completed, result.PayloadSnapshot, ct).ConfigureAwait(false);
                executed.Add((step, log));

                if (result.UpstreamResult is { Success: true, Body: not null } upstream)
                {
                    context.LastUpstreamResult = upstream;
                }
            }
            catch (Exception ex)
            {
                log.MarkFailed();
                await _logRepo.UpdateStateAsync(log.Id, SagaState.Failed, null, ct).ConfigureAwait(false);

                _logger.LogError(ex, "Saga step {Step} threw for correlation {Corr}",
                    step.Name, context.CorrelationId);

                await CompensateAsync(context, executed, ct).ConfigureAwait(false);

                return new GatewayResponse
                {
                    StatusCode = 502,
                    Body = "Saga step threw an exception",
                    ContentType = "text/plain"
                };
            }
        }

        return new GatewayResponse
        {
            StatusCode = 200,
            Body = context.AggregatedResponse ?? context.LastUpstreamResult?.Body ?? "{}",
            ContentType = "application/json"
        };
    }

    /// <summary>
    /// Walks executed steps in reverse order, invoking ICompensatableStep
    /// on each. Steps that are not compensatable are skipped (logged at
    /// warning so operators know which side effects may be left in an
    /// inconsistent state).
    /// </summary>
    private async Task CompensateAsync(SagaContext context, IReadOnlyList<(ISagaStep Step, SagaExecutionLog Log)> executed, CancellationToken ct)
    {
        _logger.LogWarning("Initiating compensation walk for correlation {Corr} ({Count} steps)",
            context.CorrelationId, executed.Count);

        for (var i = executed.Count - 1; i >= 0; i--)
        {
            var (step, log) = executed[i];

            if (step is not ICompensatableStep compensatable)
            {
                _logger.LogWarning("Step {Step} is not compensatable — side effect may persist. Correlation {Corr}",
                    step.Name, context.CorrelationId);
                continue;
            }

            try
            {
                log.MarkCompensating();
                await _logRepo.UpdateStateAsync(log.Id, SagaState.Compensating, log.PayloadSnapshot, ct).ConfigureAwait(false);

                await compensatable.CompensateAsync(context, log.PayloadSnapshot, ct).ConfigureAwait(false);

                log.MarkCompensated();
                await _logRepo.UpdateStateAsync(log.Id, SagaState.Compensated, log.PayloadSnapshot, ct).ConfigureAwait(false);

                _logger.LogInformation("Compensated step {Step} for correlation {Corr}", step.Name, context.CorrelationId);
            }
            catch (Exception ex)
            {
                log.MarkFailed();
                await _logRepo.UpdateStateAsync(log.Id, SagaState.Failed, log.PayloadSnapshot, ct).ConfigureAwait(false);

                _logger.LogError(ex, "Compensation failed for step {Step}, correlation {Corr} — manual intervention required",
                    step.Name, context.CorrelationId);
            }
        }
    }

    /// <summary>
    /// Recovery entry point: invoked by the BackgroundService on process
    /// restart to compensate sagas whose steps were left in non-terminal
    /// state by a crash. Loads completed steps via sp_CompensateSaga and
    /// runs each compensatable handler.
    /// </summary>
    public async Task RecoverAsync(CorrelationId correlationId, SagaContext context, CancellationToken ct = default)
    {
        var steps = await _logRepo.GetCompensatableStepsAsync(correlationId, ct).ConfigureAwait(false);
        if (steps.Count == 0)
        {
            _logger.LogInformation("RecoverAsync: no compensatable steps for {Corr}", correlationId);
            return;
        }

        var stepLookup = _steps.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps)
        {
            if (!stepLookup.TryGetValue(step.StepName, out var impl))
            {
                _logger.LogWarning("RecoverAsync: step {Step} not registered in orchestrator — skipping", step.StepName);
                continue;
            }

            if (impl is not ICompensatableStep compensatable)
            {
                _logger.LogWarning("RecoverAsync: step {Step} is not compensatable — side effect may persist", step.StepName);
                continue;
            }

            try
            {
                await _logRepo.UpdateStateAsync(step.Id, SagaState.Compensating, step.PayloadSnapshot, ct).ConfigureAwait(false);
                await compensatable.CompensateAsync(context, step.PayloadSnapshot, ct).ConfigureAwait(false);
                await _logRepo.UpdateStateAsync(step.Id, SagaState.Compensated, step.PayloadSnapshot, ct).ConfigureAwait(false);
                _logger.LogInformation("RecoverAsync compensated step {Step} for {Corr}", step.StepName, correlationId);
            }
            catch (Exception ex)
            {
                await _logRepo.UpdateStateAsync(step.Id, SagaState.Failed, step.PayloadSnapshot, ct).ConfigureAwait(false);
                _logger.LogError(ex, "RecoverAsync: step {Step} compensation failed for {Corr}", step.StepName, correlationId);
            }
        }
    }
}
