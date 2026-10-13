namespace WPMT.Core.Exceptions;

public class CompressionException : WPMTException
{
    public CompressionException() : base() { }
    public CompressionException(string message) : base(message) { }
    public CompressionException(string message, Exception inner) : base(message, inner) { }
}
