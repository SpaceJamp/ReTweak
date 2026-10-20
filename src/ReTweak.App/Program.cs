using ReTweak.Core;

namespace ReTweak.App;

/// <summary>
/// Entry point. ReTweak.exe has one extra mode, the fan watchdog (<see cref="FanWatchdog"/>), which must run
/// without starting WinUI; everything else goes to the normal XAML startup.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is [FanWatchdog.Arg, var pid]) return FanWatchdog.Run(pid);
        XamlGeneratedProgram.XamlGeneratedMain();
        return 0;
    }
}
