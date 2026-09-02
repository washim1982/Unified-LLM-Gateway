using UnifiedGateway.Models;

namespace UnifiedGateway.Services;

public interface IGuardrailService
{
    /// <summary>
    /// Inspect inbound prompts for PCI, PII, Secrets, and Prompt Injection before routing to models.
    /// </summary>
    Task<GuardrailResult> EvaluateAsync(
        string input,
        string? systemPrompt = null,
        GuardrailActionMode? modeOverride = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inspect outbound LLM responses for leaked PCI, PII, or Secrets before returning to caller.
    /// </summary>
    Task<GuardrailResult> EvaluateOutputAsync(
        string output,
        GuardrailActionMode? modeOverride = null,
        CancellationToken cancellationToken = default);

    GuardrailOptions GetCurrentOptions();
    void UpdateOptions(GuardrailOptions options);
}
