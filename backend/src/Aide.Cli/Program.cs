using System.Reflection;

// `aide` CLI (SPEC FR-52). Stub: prints its version until the commands are built.
var informational = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
var version = informational.Split('+')[0];

Console.WriteLine($"aide {version}");
return 0;
