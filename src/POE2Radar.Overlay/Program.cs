using POE2Radar.Core;
using POE2Radar.Overlay;

Console.WriteLine("POE2Radar — map/radar overlay");
Console.WriteLine("=============================");

using var process = ProcessHandle.AttachToPoE();
if (process is null)
{
    Console.Error.WriteLine("PoE2 not running (no matching process found).");
    return 1;
}
Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

var reader = new MemoryReader(process);

var slot = Bootstrap.ResolveGameStateSlot(process, reader);
if (slot == 0)
    return 2;

Console.WriteLine();
Console.WriteLine("Radar running. Open the in-game map to see terrain + entities.");
Console.WriteLine("Atlas: open it in-game; rings are auto-positioned. F10 over a tile = inspect its map/content/biome.");
Console.WriteLine("Ctrl+C to exit.");

using var app = new RadarApp(process, reader, slot);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; app.RequestShutdown(); };
app.Run();

Console.WriteLine("Done.");
return 0;
