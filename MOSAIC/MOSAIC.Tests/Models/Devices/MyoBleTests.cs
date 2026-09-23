using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Interfaces;
using MOSAIC.Components.Manager.BLE;
using MOSAIC.Components.Manager.BLE.Commands;
using MOSAIC.Models.Devices;

namespace MOSAIC.Tests.Models.Devices;

[TestClass]
public class MyoBleTests
{
    [TestMethod]
    public async Task RetainedSamplesSurviveFurtherTicksAndMissingPackets()
    {
        var peripheral = new Peripheral();
        var manager = await ManagerFor(peripheral);
        await using var myo = new Myo(manager);
        var capture = new RetainingCapture();
        myo.AddSubscriber(capture);
        var connect = myo.ConnectAsync(peripheral.Name).AsTask();
        peripheral.Emg[0].Emit(Enumerable.Repeat((byte)16, 8).Concat(Enumerable.Repeat((byte)32, 8)).ToArray());
        Assert.IsTrue(await connect);
        var retained = new List<Vector<double>>();
        for (int tick = 0; tick < 5; tick++)
        {
            myo.ReceiveInput(this, tick / 200d);
            retained.Add(await capture.Values.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        }
        Assert.AreEqual(16 / 128d, retained[0][0]);
        for (int tick = 1; tick < retained.Count; tick++)
            Assert.AreEqual(32 / 128d, retained[tick][0], "An empty BLE queue must retain the latest sample.");
        Assert.AreNotSame(retained[0], retained[2]);
        Assert.AreNotSame(retained[1], retained[3]);
    }

    private sealed class RetainingCapture : ISubscriber
    {
        public Channel<Vector<double>> Values { get; } = Channel.CreateUnbounded<Vector<double>>();
        public void ReceiveInput(object sender, object value) => Values.Writer.TryWrite((Vector<double>)value);
    }

    [TestMethod]
    public async Task ConnectionWaitsForSampleThenPublishesBothEmgSamplesOnTicks()
    {
        var peripheral = new Peripheral();
        var manager = await ManagerFor(peripheral);
        using var myo = new Myo(manager);
        var capture = new Capture();
        myo.AddSubscriber(capture);
        var connect = myo.ConnectAsync(peripheral.Name).AsTask();
        Assert.IsFalse(connect.IsCompleted, "BLE connection alone must not count as streaming.");
        Assert.IsFalse(myo.IsConnected);
        Assert.IsTrue(peripheral.Emg.All(c => c.NotificationsStarted));
        Assert.HasCount(4, peripheral.Commands.Writes);
        Assert.AreEqual(4, peripheral.Commands.ControlWriteCount, "Myo setup must use the backend's control-write policy.");

        peripheral.Emg[0].Emit([0, 127, 128, 255, 64, 192, 32, 224, 1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.IsTrue(await connect);
        Assert.IsTrue(myo.IsConnected);
        myo.ReceiveInput(this, 0.0);
        var first = await capture.Values.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        myo.ReceiveInput(this, 0.005);
        var second = await capture.Values.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new[] { 0.0, 127 / 128.0, -1.0, -1 / 128.0, 0.5, -0.5, 0.25, -0.25 }, first);
        CollectionAssert.AreEqual(Enumerable.Range(1, 8).Select(x => x / 128.0).ToArray(), second);
    }

    [TestMethod]
    public async Task FailedNotificationDoesNotSendStreamingCommandOrReportConnected()
    {
        var peripheral = new Peripheral();
        peripheral.Emg[1].NotificationError = new InvalidOperationException("CCCD rejected");
        var manager = await ManagerFor(peripheral);
        using var myo = new Myo(manager);

        Assert.IsFalse(await myo.ConnectAsync(peripheral.Name));
        Assert.IsFalse(myo.IsConnected);
        Assert.AreEqual("CCCD rejected", myo.LastConnectionError);
        Assert.IsTrue(peripheral.DisconnectCalled);
        Assert.AreEqual(0, peripheral.Emg[1].SubscriberCount, "Failed setup must detach its data handler.");
        Assert.IsFalse(peripheral.Emg[2].NotificationsStarted);
        Assert.HasCount(2, peripheral.Commands.Writes, "Only vibration commands should have been sent.");
        Assert.IsEmpty(manager.ConnectedDevices);
    }

    [TestMethod]
    public async Task MissingEmgCharacteristicFailsInsteadOfSilentlyContinuing()
    {
        var peripheral = new Peripheral { MissingEmgIndex = 0 };
        var manager = await ManagerFor(peripheral);
        using var myo = new Myo(manager);
        Assert.IsFalse(await myo.ConnectAsync(peripheral.Name));
        StringAssert.Contains(myo.LastConnectionError!, "not found");
        Assert.IsTrue(peripheral.DisconnectCalled);
    }

    [TestMethod]
    public async Task CancellationWhileWaitingForDataDisconnectsAndPropagates()
    {
        var peripheral = new Peripheral();
        var manager = await ManagerFor(peripheral);
        using var myo = new Myo(manager);
        using var ct = new CancellationTokenSource();
        var connect = myo.ConnectAsync(peripheral.Name, ct.Token).AsTask();
        ct.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => connect);
        Assert.IsFalse(myo.IsConnected);
        Assert.IsTrue(peripheral.DisconnectCalled);
    }

    [TestMethod]
    public async Task NoDataReportsExplicitTimeoutAndClosesConnection()
    {
        var peripheral = new Peripheral();
        var manager = await ManagerFor(peripheral);
        using var myo = new Myo(manager);
        Assert.IsFalse(await myo.ConnectAsync(peripheral.Name));
        StringAssert.Contains(myo.LastConnectionError!, "sent no EMG data");
        Assert.IsTrue(peripheral.DisconnectCalled);
    }

    [TestMethod]
    public async Task DisconnectStillClosesLinkWhenVibrationFails()
    {
        var peripheral = new Peripheral();
        var manager = await ManagerFor(peripheral);
        using var myo = new Myo(manager);
        var connect = myo.ConnectAsync(peripheral.Name).AsTask();
        peripheral.Emg[0].Emit(new byte[16]);
        Assert.IsTrue(await connect);
        peripheral.Commands.WriteError = new InvalidOperationException("Disconnected during vibration");
        await myo.DisconnectAsync();
        Assert.IsFalse(myo.IsConnected);
        Assert.IsTrue(peripheral.DisconnectCalled);
        Assert.IsEmpty(manager.ConnectedDevices);
    }

    [TestMethod]
    public async Task EmptyNotificationDoesNotCountAsFirstSample()
    {
        var device = new BleDevice("Myo", new Peripheral(), 8);
        device.HandleCharacteristicValue([]);
        await Assert.ThrowsAsync<TimeoutException>(() => device.WaitForFirstSampleAsync(TimeSpan.Zero));
        device.HandleCharacteristicValue(new byte[16]);
        await device.WaitForFirstSampleAsync(TimeSpan.Zero);
        Assert.HasCount(2, device.SampleBuffer);
    }

    private static async Task<BleManager> ManagerFor(Peripheral peripheral)
    {
        var manager = new BleManager(new Backend(peripheral));
        await manager.ScanForBleDevicesAsync(0);
        return manager;
    }

    private sealed class Capture : ISubscriber
    {
        public Channel<double[]> Values { get; } = Channel.CreateUnbounded<double[]>();
        public void ReceiveInput(object sender, object value) => Values.Writer.TryWrite(((Vector<double>)value).ToArray());
    }

    private sealed class Backend(Peripheral peripheral) : IBleBackend
    {
        public Task<bool> GetAvailabilityAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<BleDiscoveredDevice>> ScanAsync(int timeMs, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BleDiscoveredDevice>>([
                new BleDiscoveredDevice { Id = peripheral.Id, Name = peripheral.Name, Peripheral = peripheral }
            ]);
    }

    private sealed class Peripheral : IBlePeripheral
    {
        public string Id => "test-myo";
        public string Name => "Myo Test";
        public bool IsConnected { get; private set; }
        public bool DisconnectCalled { get; private set; }
        public int MissingEmgIndex { get; init; } = -1;
        public Characteristic Commands { get; } = new(Guid.Parse("d5060401-a904-deb9-4748-2c7f4a124842"));
        public Characteristic[] Emg { get; } = new MyoCommandDefinitions().GetInitialNotificationCommands()
            .Select(c => new Characteristic(Guid.Parse(c.Characteristic))).ToArray();
        public Task ConnectAsync(CancellationToken cancellationToken = default) { IsConnected = true; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            DisconnectCalled = true;
            return Task.CompletedTask;
        }
        public Task<IBleCharacteristic?> GetCharacteristicAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken = default)
            => Task.FromResult<IBleCharacteristic?>(characteristicUuid == Commands.Uuid ? Commands
                : Emg.Where((_, i) => i != MissingEmgIndex).FirstOrDefault(c => c.Uuid == characteristicUuid));
    }

    private sealed class Characteristic(Guid uuid) : IBleCharacteristic
    {
        public Guid Uuid => uuid;
        public event EventHandler<byte[]>? ValueChanged;
        public int SubscriberCount => ValueChanged?.GetInvocationList().Length ?? 0;
        public bool NotificationsStarted { get; private set; }
        public Exception? NotificationError { get; set; }
        public Exception? WriteError { get; set; }
        public List<byte[]> Writes { get; } = [];
        public int ControlWriteCount { get; private set; }
        public void Emit(byte[] value) => ValueChanged?.Invoke(this, value);
        public Task WriteValueAsync(byte[] value, CancellationToken cancellationToken = default)
        {
            ControlWriteCount++;
            return WriteValueWithoutResponseAsync(value, cancellationToken);
        }
        public Task WriteValueWithoutResponseAsync(byte[] value, CancellationToken cancellationToken = default)
        {
            if (WriteError is not null) throw WriteError;
            Writes.Add(value);
            return Task.CompletedTask;
        }
        public Task StartNotificationsAsync(CancellationToken cancellationToken = default)
        {
            if (NotificationError is not null) throw NotificationError;
            NotificationsStarted = true;
            return Task.CompletedTask;
        }
    }
}
