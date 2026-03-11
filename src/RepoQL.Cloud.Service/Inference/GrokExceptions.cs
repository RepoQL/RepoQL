using Grpc.Core;

namespace RepoQL.Cloud.Service.Inference;

/// <summary>
/// Purpose: Preserve Grok failure intent so the gRPC surface can map errors cleanly.
/// Complexity: Small exception types that distinguish timeout versus provider failure.
/// </summary>
internal class GrokException : Exception
{
    public GrokException()
    {
    }

    public GrokException(string message)
        : base(message)
    {
    }

    public GrokException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class GrokTimeoutException : GrokException
{
    public GrokTimeoutException()
    {
    }

    public GrokTimeoutException(string message)
        : base(message)
    {
    }

    public GrokTimeoutException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class GrokApiException : GrokException
{
    public GrokApiException()
    {
    }

    public GrokApiException(string message)
        : base(message)
    {
    }

    public GrokApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GrokApiException(string message, StatusCode statusCode, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public StatusCode StatusCode { get; }
}
