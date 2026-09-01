using UnifiedGateway.Models;

namespace UnifiedGateway.Services;

public interface IGuardrailService
{
    Task<GuardrailResult> EvaluateAsync(
        string input,
        string? systemPrompt = null,
        GuardrailActionMode? modeOverride = null,
        CancellationToken cancellationToken = default);

    GuardrailOptions GetCurrentOptions();
    void UpdateOptions(GuardrailOptions options);
}
