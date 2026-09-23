using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Manager.BLE;

namespace MOSAIC.Tests.Components.Manager.BLE;

[TestClass]
public class GattSubscriptionConfigurationTests
{
    [TestMethod]
    [DataRow(true, false, (byte)0x01)]
    [DataRow(false, true, (byte)0x02)]
    [DataRow(true, true, (byte)0x02)]
    public void UsesAdvertisedStreamingMode(bool notify, bool indicate, byte expected)
        => CollectionAssert.AreEqual(new byte[] { expected, 0 }, GattSubscriptionConfiguration.Select(notify, indicate));

    [TestMethod]
    public void RejectsCharacteristicsWithoutAStreamingCapability()
        => Assert.Throws<InvalidOperationException>(() => GattSubscriptionConfiguration.Select(false, false));
}
