namespace Clash.ProxyNet;

/// <summary>
/// Represents an error that occurred during proxy protocol negotiation.
/// </summary>
internal class ProxyProtocolException : Exception
{
    public ProxyErrorCode ErrorCode { get; }

    internal ProxyProtocolException(ProxyErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    internal ProxyProtocolException(ProxyErrorCode errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    internal ProxyProtocolException(ProxyErrorCode errorCode)
    {
        ErrorCode = errorCode;
    }
}

internal enum ProxyErrorCode
{
    AuthRequired,
    AuthFailed,
    ConnectionFailed,
    InvalidResponse,
    Timeout,
    StringTooLong,
    TransportUpgradeFailed,
}
