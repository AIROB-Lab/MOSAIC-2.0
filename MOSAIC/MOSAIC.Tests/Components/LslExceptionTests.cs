using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MOSAIC.Tests.Components;

[TestClass]
public class LslExceptionTests
{
    [TestMethod]
    public void LostExceptionPreservesDiagnosticCause()
    {
        var cause = new InvalidOperationException("Socket closed");
        var exception = new global::LSL.LostException("EEG stream disconnected", cause);

        Assert.AreEqual("EEG stream disconnected", exception.Message);
        Assert.AreSame(cause, exception.InnerException);
        StringAssert.Contains(exception.ToString(), "Socket closed");
    }

    [TestMethod]
    public void InternalExceptionPreservesDiagnosticCause()
    {
        var cause = new InvalidOperationException("Native stream error");
        var exception = new global::LSL.InternalException("Could not read sample", cause);

        Assert.AreEqual("Could not read sample", exception.Message);
        Assert.AreSame(cause, exception.InnerException);
        StringAssert.Contains(exception.ToString(), "Native stream error");
    }

    [TestMethod]
    public void ExceptionsAcceptAnOmittedInnerException()
    {
        Assert.IsNull(new global::LSL.LostException("Disconnected").InnerException);
        Assert.IsNull(new global::LSL.InternalException("Failed").InnerException);
    }
}
