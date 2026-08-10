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

        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        Application.Run(new SnippingApplicationContext());
    }    
}
