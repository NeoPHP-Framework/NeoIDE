using Velopack;

namespace NeoIDE;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles the install / update / uninstall hooks of Velopack and applies an update
        // downloaded during the previous session before the IDE starts.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
