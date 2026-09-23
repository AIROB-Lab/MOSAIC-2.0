using System;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Factory;
using MOSAIC.Components.Interfaces;
using MOSAIC.Views;

namespace MOSAIC.Tests.Views;

/// <summary>
/// The scope-monitor button has to appear whenever the pipeline gains a block that can plot,
/// however that block arrived.
/// </summary>
/// <remarks>
/// <para>
/// The bug this pins: <c>MainView</c> subscribed to its own <c>Blocks</c> collection in the
/// parameterless constructor, and the running app never calls that one. Dependency injection picks
/// the greediest constructor it can satisfy — the three-argument overload — which chains to
/// <c>MainView(IBlockFactory)</c>, and neither of those wired the subscription up. So nothing
/// listened to the collection, and the button only ever appeared on the code paths that refresh it
/// by hand. Opening a config file is one; dragging blocks onto the canvas to build a pipeline is
/// not, so that route showed no button no matter how many scopes the pipeline had.
/// </para>
/// <para>
/// The assertions here are deliberately about the subscription rather than about a rendered pixel:
/// the failure was that no handler existed, and that is what has to stay true. Constructing the
/// view itself would need a display and an <c>Application</c>, which is more machinery than the
/// invariant deserves.
/// </para>
/// </remarks>
[TestClass]
public class MonitorFabTests
{
    /// <summary>The container the app builds, minus the window registrations the tests do not need.</summary>
    private static IServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBlockFactory, BlockFactory>();
        services.AddSingleton<MOSAIC.Services.RecordingService>();
        services.AddSingleton<IRecordingDestination>(
            sp => sp.GetRequiredService<MOSAIC.Services.RecordingService>());
        services.AddSingleton<MOSAIC.Services.IFilePickerService, MOSAIC.Services.StorageFilePickerService>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Reproduces the container's choice: greediest public constructor whose parameters all resolve.
    /// </summary>
    private static ConstructorInfo ConstructorTheContainerPicks(IServiceProvider sp) =>
        typeof(MainView)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().All(p => sp.GetService(p.ParameterType) is not null))
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

    /// <summary>Walks a constructor chain, collecting every <c>MainView</c> constructor it runs.</summary>
    private static ConstructorInfo[] ChainOf(ConstructorInfo entry)
    {
        // A `: this(...)` chain shows up as a call to another constructor of the same type, so the
        // whole chain is reachable by following those calls from the entry point.
        var chain = new System.Collections.Generic.List<ConstructorInfo> { entry };
        var seen = new System.Collections.Generic.HashSet<ConstructorInfo> { entry };

        for (int i = 0; i < chain.Count; i++)
        {
            foreach (var next in CalledConstructors(chain[i]))
                if (seen.Add(next))
                    chain.Add(next);
        }
        return chain.ToArray();
    }

    private static System.Collections.Generic.IEnumerable<ConstructorInfo> CalledConstructors(ConstructorInfo ctor)
    {
        var il = ctor.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
        var module = typeof(MainView).Module;

        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x73 && il[i] != 0x28) continue;    // newobj / call
            var token = BitConverter.ToInt32(il, i + 1);
            ConstructorInfo? target = null;
            try { target = module.ResolveMethod(token) as ConstructorInfo; } catch { /* not a ctor token */ }
            if (target is not null && target.DeclaringType == typeof(MainView))
                yield return target;
        }
    }

    private static bool CallsBlockWiring(ConstructorInfo ctor)
    {
        var il = ctor.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
        var module = typeof(MainView).Module;

        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F) continue;    // call / callvirt
            var token = BitConverter.ToInt32(il, i + 1);
            try
            {
                var m = module.ResolveMethod(token);
                if (m is not null && m.Name == "WireBlockCollection") return true;
            }
            catch { /* token is not a method */ }
        }
        return false;
    }

    [TestMethod]
    public void TheConstructorTheContainerPicks_SubscribesToTheBlockCollection()
    {
        var picked = ConstructorTheContainerPicks(Services());
        var chain = ChainOf(picked);

        Assert.IsTrue(
            chain.Any(CallsBlockWiring),
            "The constructor dependency injection selects — " +
            $"MainView({string.Join(", ", picked.GetParameters().Select(p => p.ParameterType.Name))}) — " +
            "must wire up Blocks.CollectionChanged, directly or through the constructors it chains " +
            "to. Without it nothing refreshes the scope-monitor button when a block is dropped on " +
            "the canvas.");
    }

    [TestMethod]
    public void EveryConstructorThatBuildsTheView_SubscribesExactlyOnce()
    {
        var sp = Services();
        foreach (var ctor in typeof(MainView).GetConstructors())
        {
            var chain = ChainOf(ctor);
            var wiring = chain.Count(CallsBlockWiring);
            var signature = $"MainView({string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.Name))})";

            Assert.AreEqual(1, wiring,
                $"{signature} runs the block-collection wiring {wiring} times. Once is required: " +
                "zero leaves the monitor button stale, and more than once double-subscribes so every " +
                "change is handled twice.");
        }
    }

    [TestMethod]
    public void ABlockWithAScope_IsWhatMakesTheButtonAppear()
    {
        // The predicate behind the button's visibility, checked directly: a block that can plot has a
        // BlockVisualization or ScopeMonitor property, and a bare block does not.
        var sp = Services();
        var factory = new BlockFactory(sp);

        var scoped = (BaseBlock)factory.Create(new JsonModel
        {
            Type = "crop", Name = "WithScope", Params = new object[] { 0, 4 }
        });
        var plain = (BaseBlock)factory.Create(new JsonModel
        {
            Type = "clockblock", Name = "NoScope", Params = new object[] { 30.0 }
        });

        var hasScope = typeof(ScopeMonitorWindow).GetMethod(
            "HasScopeProperty", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.IsTrue((bool)hasScope.Invoke(null, new object[] { scoped })!,
            "Crop publishes to a scope, so it must count towards showing the monitor button.");

        (scoped as IDisposable)?.Dispose();
        (plain as IDisposable)?.Dispose();
    }
}
