using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Dissonancia;

// out.png  tab (0 START, 1 LIVE, 3 SCORE)  mode (AppMode)  time in seconds  [actions from the action table, e.g. EditRack]
if (args.Length < 4) { Console.WriteLine("usage: out.png <tab> <mode> <seconds> [action...]"); return 1; }
AppBuilder.Configure<ShotApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
var w = new MainWindow();
double t = double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
w.Clock = () => t;
var mode = Enum.Parse<AppMode>(args[2]);
w.SetMode(mode, mode == AppMode.GuitarChords ? Clef.Treble8vb : Clef.Treble);
w.Show();
w.SelectTab(int.Parse(args[1]));
for (int i = 0; i < 40; i++)   // 2 s of frames so meters, scope and waterfall fill
{
    t += 0.05;
    w.Step(t);
    if (i == 1) foreach (var a in args.Skip(4)) w.Action(a);
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
