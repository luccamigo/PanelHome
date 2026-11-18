using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PanelHost.Tests;

[TestClass]
public sealed class ProcessCrashDiagnosticsTests
{
    [TestMethod]
    public void Exception_diagnostics_include_hresult_stack_and_inner_exception()
    {
        using var log = new StringWriter();
        Exception exception;
        try
        {
            ThrowOuter();
            throw new AssertFailedException("Expected ThrowOuter to throw.");
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        ProcessCrashDiagnostics.WriteException(log, "test boundary", exception);

        var text = log.ToString();
        StringAssert.Contains(text, "boundary=test boundary");
        StringAssert.Contains(text, "System.InvalidOperationException");
        StringAssert.Contains(text, "outer failure");
        StringAssert.Contains(text, "hresult=0x80131509");
        StringAssert.Contains(text, "stack:");
        StringAssert.Contains(text, nameof(ThrowOuter));
        StringAssert.Contains(text, "System.ArgumentException");
        StringAssert.Contains(text, "inner failure");
    }

    private static void ThrowOuter()
    {
        try
        {
            throw new ArgumentException("inner failure");
        }
        catch (ArgumentException inner)
        {
            throw new InvalidOperationException("outer failure", inner);
        }
    }
}
