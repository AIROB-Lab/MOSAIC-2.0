using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Services;
using MOSAIC.Views;

namespace MOSAIC.Tests.Views;

/// <summary>
/// Guards the constructor that dependency injection actually uses for <see cref="MainView"/>.
///
/// Microsoft.Extensions.DependencyInjection resolves a type through the constructor whose parameter
/// set is a superset of every other applicable one — in practice, the greediest resolvable overload.
/// <see cref="MainView"/> has several constructors, so a service added to the wrong one is simply
/// never assigned: the field stays null and whatever it powers is silently inert, with nothing
/// failing at build or startup to say so. That is exactly how the header's recording controls
/// shipped dead once already.
///
/// Reflection only — no Avalonia initialisation, so this stays a metadata check rather than an
/// attempt to construct a control outside a running application.
/// </summary>
[TestClass]
public class MainViewConstructorTests
{
    /// <summary>The overload the container will pick: the one no other overload's parameters exceed.</summary>
    private static ConstructorInfo GreediestConstructor() =>
        typeof(MainView)
            .GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

    [TestMethod]
    public void GreediestConstructor_TakesTheServicesTheHeaderNeeds()
    {
        var parameters = GreediestConstructor()
            .GetParameters()
            .Select(p => p.ParameterType)
            .ToArray();

        // Without these, the recording-folder chip resolves nothing and clicking it does nothing.
        CollectionAssert.Contains(parameters, typeof(RecordingService),
            "MainView's DI constructor must take RecordingService, or the header chip is inert.");
        CollectionAssert.Contains(parameters, typeof(IFilePickerService),
            "MainView's DI constructor must take IFilePickerService, or the folder picker never opens.");
    }

    [TestMethod]
    public void GreediestConstructor_IsUnambiguousForTheContainer()
    {
        var greediest = GreediestConstructor().GetParameters().Select(p => p.ParameterType).ToHashSet();

        // The container only accepts a winner whose parameters cover every other overload's. If two
        // constructors ever tie on length, or one takes a service the greediest does not, resolution
        // becomes ambiguous and MainView fails to activate at startup.
        foreach (var ctor in typeof(MainView).GetConstructors())
        {
            var parameters = ctor.GetParameters().Select(p => p.ParameterType);

            Assert.IsTrue(parameters.All(greediest.Contains),
                $"Constructor ({string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.Name))}) " +
                "takes a service the DI-selected constructor does not, which makes activation ambiguous.");
        }
    }
}
