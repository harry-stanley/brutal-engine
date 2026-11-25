using BenchmarkDotNet.Running;
using S5;

var mode = args.Length > 0 ? args[0] : "test";
switch (mode)
{
    case "test":
        foreach (var m in Enum.GetValues<StorageMode>())
        {
            for (int seed = 1; seed <= 5; seed++) RandomTest.Run(m, seed, 4000);
            Console.WriteLine($"  randomized differential test OK ({m}): 5 seeds x 4000 steps");
            RandomTest.ChangeDetection(m);
        }
        // big-scale smoke: chunk16K with > 1 chunk churn
        foreach (var m in new[] { StorageMode.Chunk16K, StorageMode.Table }) RandomTest.Run(m, 99, 30000);
        Console.WriteLine("  long run OK (30000 steps, both modes)");
        break;
    case "alloc":
        int frames = args.Length > 1 ? int.Parse(args[1]) : 1000;
        foreach (var m in Enum.GetValues<StorageMode>()) AllocHarness.Run(m, frames);
        break;
    default:
        BenchmarkSwitcher.FromAssembly(typeof(World).Assembly).Run(args);
        break;
}
