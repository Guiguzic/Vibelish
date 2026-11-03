namespace InputTranslateSetup;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var silent = args.Any(a =>
            a.Equals("--silent", StringComparison.OrdinalIgnoreCase)
            || a.Equals("/S", StringComparison.OrdinalIgnoreCase));

        if (silent)
        {
            try
            {
                InstallerCore.Install(true, false, _ => { });
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        Application.Run(new SetupForm());
        return 0;
    }
}
