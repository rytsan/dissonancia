using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Dissonancia;

// out.png  tab (0 START, 1 LIVE, 3 SCORE)  mode (LiveMode)  time in seconds  [actions from the action table, e.g. EditRack]
if (args.Length < 4) { Console.WriteLine("usage: out.png <tab> <mode> <seconds> [action...]"); return 1; }
AppBuilder.Configure<ShotApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
var w = new MainWindow();
if (Environment.GetEnvironmentVariable("SHOT_H") is { Length: > 0 } sh) w.Height = int.Parse(sh);   // a taller window: the whole STUDIO rack
double t = double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
w.Clock = () => t;
var mode = Enum.Parse<LiveMode>(args[2]);
w.SetMode(mode, mode == LiveMode.Acoustic ? Clef.Treble8vb : mode is LiveMode.Melody or LiveMode.AcousticVoice ? null : Clef.Treble);   // melody lines: Auto
w.Show();
w.SelectTab(int.Parse(args[1]));
int frames = int.TryParse(Environment.GetEnvironmentVariable("SHOT_FRAMES"), out var nf) ? nf : 40;   // more: a lane that scrolls fills up
for (int i = 0; i < frames; i++)   // 2 s of frames so meters, scope and waterfall fill
{
    t += 0.05;
    w.Step(t);
    if (i == 1) foreach (var a in args.Skip(4)) { if (a.StartsWith("Wait")) Thread.Sleep(1000 * (a.Length > 4 ? int.Parse(a[4..]) : 2)); else w.Action(a); }   // WaitN: N s for a background job
    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
using var file = File.Create(args[0]);
w.CaptureRenderedFrame()!.Save(file);
Console.WriteLine("saved " + args[0]);
return 0;

class ShotApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;   // as the app
    }
}
