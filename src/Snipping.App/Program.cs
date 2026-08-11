namespace Snipping.App;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\Snipping.Capture.SingleInstance",
            createdNew: out var createdNew);
        if (!createdNew)
            return;

        // Keep screen coordinates consistent across mixed-DPI monitors.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SnippingApplicationContext());
    }    
}
