namespace WPMT.Core.Exceptions;

public class WPMTException : Exception
{
    public WPMTException() : base() { }
    public WPMTException(string message) : base(message) { }
    public WPMTException(string message, Exception inner) : base(message, inner) { }
}
