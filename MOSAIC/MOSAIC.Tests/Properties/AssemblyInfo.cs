using Microsoft.VisualStudio.TestTools.UnitTesting;

// Tests run in parallel (method level). The LiveCharts race that previously forced serial
// execution is gone: TestSupport.TestBootstrap disables BlockVisualization's chart monitors in
// [AssemblyInitialize], so no test constructs LiveCharts UI controls. See TestBootstrap.cs.
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]
