using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Models.Devices;

namespace MOSAIC.Tests.Models.Devices;

/// <summary>
/// Tests for <see cref="CommandSender"/> — the ASCII-over-UDP command block for the ESP nodes.
/// </summary>
/// <remarks>
/// UDP "connect" only records a default remote endpoint; nothing is transmitted and no
/// listener is required. That is what makes these tests hardware-free.
/// </remarks>
[TestClass]
public class CommandSenderTests
{
    private static IServiceProvider Services() => new ServiceCollection().BuildServiceProvider();

    private static JsonModel Model(params object[] parameters) =>
        new() { Type = "CommandSender", Name = "ESP-Control", Params = parameters };

    [TestMethod]
    public void ConfigureInput_ConnectsImmediately()
    {
        // The legacy block connected inside ConfigureInputs(). Without this, a freshly
        // loaded pipeline throws "not connected" on the first SendCommand.
        using var block = CommandSender.ConfigureInput(Services(), Model("127.0.0.1", 11000));

        Assert.IsTrue(block.IsConnected);
        Assert.AreEqual("Connected", block.Info.Status);
        Assert.AreEqual("127.0.0.1:11000", block.Info.Target);
    }

    [TestMethod]
    public void ConfigureInput_ReadsHostAndPort()
    {
        using var block = CommandSender.ConfigureInput(Services(), Model("192.168.0.255", 11000));

        Assert.AreEqual("192.168.0.255", block.Host);
        Assert.AreEqual(11000, block.Port);
    }

    [TestMethod]
    public void ConfigureInput_WithABadHost_RecordsTheFailureInsteadOfThrowing()
    {
        // One unreachable address must not abort the whole pipeline load.
        using var block = CommandSender.ConfigureInput(Services(), Model("not a host", 11000));

        Assert.IsFalse(block.IsConnected);
        Assert.StartsWith("Connection failed", block.Info.Status);
    }

    [TestMethod]
    public void ConfigureInput_WithANonNumericPort_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => CommandSender.ConfigureInput(Services(), Model("127.0.0.1", "not-a-port")));
    }

    [TestMethod]
    public void ConfigureInput_WithoutParams_Throws()
    {
        // Defaulting a missing address is worse than failing. A UDP send to a subnet that does
        // not exist is indistinguishable from a successful one, so the block used to spend the
        // whole session addressing someone else's lab while reporting "Connected".
        Assert.ThrowsExactly<ArgumentException>(
            () => CommandSender.ConfigureInput(Services(), Model()));

        Assert.ThrowsExactly<ArgumentException>(
            () => CommandSender.ConfigureInput(
                Services(), new JsonModel { Type = "CommandSender", Name = "ESP-Control" }));
    }

    [TestMethod]
    public void ConfigureInput_WithTheWrongParamCount_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => CommandSender.ConfigureInput(Services(), Model("192.168.0.255")));

        Assert.ThrowsExactly<ArgumentException>(
            () => CommandSender.ConfigureInput(Services(), Model("192.168.0.255", 11000, "extra")));
    }

    [TestMethod]
    public void ConfigureInput_WithAnEmptyHost_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => CommandSender.ConfigureInput(Services(), Model("   ", 11000)));
    }

    [TestMethod]
    public void ConfigureInput_WithAnOutOfRangePort_Throws()
    {
        // Caught here rather than inside Connect(), where TryConnect would swallow it into a
        // "Connection failed" status that reads like a network problem.
        Assert.ThrowsExactly<ArgumentException>(
            () => CommandSender.ConfigureInput(Services(), Model("192.168.0.255", 70000)));

        Assert.ThrowsExactly<ArgumentException>(
            () => CommandSender.ConfigureInput(Services(), Model("192.168.0.255", 0)));
    }

    [TestMethod]
    public void Connect_EnablesBroadcast()
    {
        // The ESP mesh is addressed by subnet broadcast (x.x.x.255), which some stacks
        // refuse unless the socket opts in.
        using var block = CommandSender.ConfigureInput(Services(), Model("192.168.0.255", 11000));

        Assert.IsTrue(block.IsConnected, "broadcast address should be connectable");
    }

    [TestMethod]
    public void Disconnect_ClearsTheConnectionButKeepsTheBlockReusable()
    {
        using var block = CommandSender.ConfigureInput(Services(), Model("127.0.0.1", 11000));

        block.Disconnect();
        Assert.IsFalse(block.IsConnected);
        Assert.AreEqual("Disconnected", block.Info.Status);

        block.Host = "127.0.0.1";
        block.Port = 11001;
        block.Connect();

        Assert.IsTrue(block.IsConnected);
        Assert.AreEqual("127.0.0.1:11001", block.Info.Target);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task SendCommand_FormatsTheAsciiCommandAndCounts()
    {
        using var block = CommandSender.ConfigureInput(Services(), Model("127.0.0.1", 11000));

        await block.SendCommand(EspStreamType.ROT, 1, enable: true);
        Assert.AreEqual("ROT1on", block.Info.LastCommand);

        await block.SendCommand(EspStreamType.ACC, 3, enable: false);
        Assert.AreEqual("ACC3off", block.Info.LastCommand);

        Assert.AreEqual(2, block.Info.CommandsSent);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task SendCommand_AfterDisconnect_Throws()
    {
        using var block = CommandSender.ConfigureInput(Services(), Model("127.0.0.1", 11000));
        block.Disconnect();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => block.SendCommand(EspStreamType.ROT, 1, true));
    }

    [TestMethod]
    public void Block_TakesNoPipelineInputs()
    {
        using var block = CommandSender.ConfigureInput(Services(), Model("127.0.0.1", 11000));

        Assert.AreEqual(0, block.MinInputs);
        Assert.AreEqual(0, block.MaxInputs);
    }
}
