# NXTest.Runtime

The execution engine and runner for the NXTest testing framework. Regular tests use
Microsoft.Testing.Platform; direct benchmark runs bypass it.

The source generator normally supplies the entry point. Custom hosts can call the same
dispatcher explicitly:

```csharp
using NXTest.Generated;
using NXTest.Runtime;

return await TestFramework.RunAsync(args, TestRegistry.GetAllTests());
```

`TestExecutionOptions` controls parallelism (`ParallelMode`), stop-on-first-failure, and
related behavior.

Most users should install the **NXTest** meta-package, which includes this package along
with the core attributes and source generator.
