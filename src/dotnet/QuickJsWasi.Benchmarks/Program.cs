using BenchmarkDotNet.Running;

// Run every benchmark class by default; pass BenchmarkDotNet args (e.g. --filter *AsyncBridge*) to narrow.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args.Length == 0 ? ["--filter", "*"] : args);
