using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Manager.BLE;

namespace MOSAIC.Tests.Components.Manager.BLE;

[TestClass]
public class GattWriteConfigurationTests
{
    [TestMethod]
    [DataRow(true, false, true, true)]
    [DataRow(true, true, true, true)]
    [DataRow(false, true, true, false)]
    [DataRow(true, false, false, true)]
    [DataRow(true, true, false, false)]
    [DataRow(false, true, false, false)]
    public void ChoosesSupportedWriteType(bool response, bool noResponse, bool preferResponse, bool expected)
        => Assert.AreEqual(expected, GattWriteConfiguration.UseResponse(response, noResponse, preferResponse));

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RejectsNonWritableCharacteristic(bool preferResponse)
        => Assert.Throws<InvalidOperationException>(() => GattWriteConfiguration.UseResponse(false, false, preferResponse));
}
