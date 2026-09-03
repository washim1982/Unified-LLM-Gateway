namespace UnifiedGateway.Services;

/// <summary>
/// Reference pricing catalog for LLM foundation models ($ per 1 Million Tokens).
/// </summary>
public static class ModelPricingCatalog
{
    public record ModelRate(double InputCostPerMillion, double OutputCostPerMillion);

    public static ModelRate GetDefaultRate(string? provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(provider) || provider.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            return new ModelRate(0.00, 0.00);
        }

        var m = (model ?? string.Empty).ToLowerInvariant();

        // Anthropic Claude Family (AWS Bedrock)
        if (m.Contains("claude-3-5-sonnet") || m.Contains("claude-3.5-sonnet"))
            return new ModelRate(3.00, 15.00);

        if (m.Contains("claude-3-5-haiku") || m.Contains("claude-3.5-haiku"))
            return new ModelRate(0.80, 4.00);

        if (m.Contains("claude-3-haiku"))
            return new ModelRate(0.25, 1.25);

        if (m.Contains("claude-3-opus"))
            return new ModelRate(15.00, 75.00);

        // Amazon Nova Family (AWS Bedrock)
        if (m.Contains("nova-pro"))
            return new ModelRate(0.80, 3.20);

        if (m.Contains("nova-lite"))
            return new ModelRate(0.06, 0.24);

        if (m.Contains("nova-micro"))
            return new ModelRate(0.035, 0.14);

        // Meta Llama 3 & 3.1 & 3.2 Family (AWS Bedrock)
        if (m.Contains("llama3-2-90b") || m.Contains("llama3-1-70b") || m.Contains("llama3-70b") || m.Contains("llama-3.1-70b") || m.Contains("llama-3.2-90b"))
            return new ModelRate(0.72, 0.72);

        if (m.Contains("llama3-2-11b") || m.Contains("llama3-1-8b") || m.Contains("llama3-8b") || m.Contains("llama-3.1-8b") || m.Contains("llama-3.2-11b") || m.Contains("llama-3.2-1b") || m.Contains("llama-3.2-3b"))
            return new ModelRate(0.15, 0.15);

        // Mistral AI (AWS Bedrock)
        if (m.Contains("mistral-large"))
            return new ModelRate(2.00, 6.00);

        if (m.Contains("mistral-7b") || m.Contains("mixtral-8x7b"))
            return new ModelRate(0.15, 0.15);

        // Amazon Titan
        if (m.Contains("titan-text-express"))
            return new ModelRate(0.20, 0.60);

        if (m.Contains("titan-text-lite"))
            return new ModelRate(0.15, 0.20);

        // Cohere Command
        if (m.Contains("command-r-plus") || m.Contains("command-r+"))
            return new ModelRate(3.00, 15.00);

        if (m.Contains("command-r"))
            return new ModelRate(0.50, 1.50);

        // Standard Default
        return new ModelRate(3.00, 15.00);
    }
}
