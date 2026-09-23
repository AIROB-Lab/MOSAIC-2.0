using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Services;
using MOSAIC.Views;
using MOSAIC.Views.Cards;
using MOSAIC.Visualization;

namespace MOSAIC.Tests.Views;

[TestClass]
public class MobilePopoutSafetyTests
{
    // Only the host's lifetime type matters. No desktop windowing backend is started.
    public class LifetimeProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => throw new InvalidOperationException("The capability check must not access lifetime members.");
    }

    [TestMethod]
    public void DesktopLifetimeAllowsMonitorWindows()
    {
        var lifetime = DispatchProxy.Create<IClassicDesktopStyleApplicationLifetime, LifetimeProxy>();
        Assert.IsTrue(PopoutHelper.SupportsDesktopWindows(lifetime));
    }

    [TestMethod]
    public void MobileLayoutDisablesMonitorEvenInsideADesktopPreview()
    {
        var lifetime = DispatchProxy.Create<IClassicDesktopStyleApplicationLifetime, LifetimeProxy>();
        Assert.IsFalse(PopoutHelper.SupportsDesktopWindows(lifetime, mobileLayout: true));
    }

    [TestMethod]
    public void SingleViewLifetimeNeverAllowsDesktopWindows()
    {
        var lifetime = DispatchProxy.Create<ISingleViewApplicationLifetime, LifetimeProxy>();
        Assert.IsFalse(PopoutHelper.SupportsDesktopWindows(lifetime));
        Assert.IsFalse(PopoutHelper.SupportsDesktopWindows(lifetime, mobileLayout: true));
    }

    [TestMethod]
    public void MissingOrUnknownLifetimeFailsClosed()
    {
        Assert.IsFalse(PopoutHelper.SupportsDesktopWindows(null));
        var lifetime = DispatchProxy.Create<IApplicationLifetime, LifetimeProxy>();
        Assert.IsFalse(PopoutHelper.SupportsDesktopWindows(lifetime));
    }

    [TestMethod]
    public void MobileMonitorClickReturnsBeforeTouchingWindowOrBlockState()
    {
        // The uninitialised object deliberately has no controls, blocks or window fields.
        // Any work before the guard would fail, reproducing the unsafe click path.
        var view = (MainView)RuntimeHelpers.GetUninitializedObject(typeof(MainView));
        typeof(MainView).GetField("_mobileLayoutConfigured", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(view, true);
        InvokeClick(view, "OnMonitorFabClicked");
        Assert.IsNull(typeof(MainView).GetField("_monitorWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view));
    }

    [TestMethod]
    public void OtherPopoutEntryPointsAreSafeWithoutADesktopHost()
    {
        Assert.IsNull(Application.Current, "This test must run without a desktop application.");
        Assert.IsFalse(PopoutHelper.IsDesktop);
        var panel = (VisualizationPanel)RuntimeHelpers.GetUninitializedObject(typeof(VisualizationPanel));
        InvokeClick(panel, "OnPopoutClicked");
        var card = (PopoutCardBase)RuntimeHelpers.GetUninitializedObject(typeof(PopoutCardBase));
        card.PopOutCard();
        var row = (ExpanderItem)RuntimeHelpers.GetUninitializedObject(typeof(ExpanderItem));
        InvokeClick(row, "OnPopoutClicked");
        Assert.IsFalse(PopoutHelper.TryPopout(null!));
        Assert.IsFalse(PopoutHelper.TryPopoutCopy<NeverConstructControl>(null));
    }

    public class NeverConstructControl : Control
    {
        public NeverConstructControl() => throw new InvalidOperationException("A mobile popout must not create content.");
    }

    private static void InvokeClick(object control, string method)
        => control.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(control, [null, new RoutedEventArgs()]);
}
