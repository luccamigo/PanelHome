namespace PanelOS.DriverInterop;

public enum PanelDisplayDriverFailureKind
{
    NotFound,
    AccessDenied,
    IoError,
}

public sealed class PanelDisplayDriverException : Exception
{
    public PanelDisplayDriverException(
        PanelDisplayDriverFailureKind kind,
        string operation,
        string message,
        int? win32ErrorCode,
        string? interfacePath,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Operation = operation;
        Win32ErrorCode = win32ErrorCode;
        InterfacePath = interfacePath;
    }

    public PanelDisplayDriverFailureKind Kind { get; }

    public string Operation { get; }

    public int? Win32ErrorCode { get; }

    public string? InterfacePath { get; }
}
