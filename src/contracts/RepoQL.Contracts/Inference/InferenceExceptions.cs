namespace RepoQL.Contracts.Inference;

/// <summary>
/// Purpose: Base exception for inference client failures exposed to host consumers.
/// Complexity: Provides a shared type for transport and availability failure variants.
/// </summary>
public class InferenceException : Exception
{
    public InferenceException(string message)
        : base(message)
    {
    }

    public InferenceException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Purpose: Signals that the inference service could not be reached or is unavailable.
/// Complexity: Simple semantic subtype for retryable availability failures.
/// </summary>
public sealed class InferenceUnavailableException : InferenceException
{
    public InferenceUnavailableException(string message)
        : base(message)
    {
    }

    public InferenceUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Purpose: Signals that an inference call exceeded its allowed execution time.
/// Complexity: Simple semantic subtype for timeout-specific handling.
/// </summary>
public sealed class InferenceTimeoutException : InferenceException
{
    public InferenceTimeoutException(string message)
        : base(message)
    {
    }

    public InferenceTimeoutException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
