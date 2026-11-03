namespace VibelishSetup;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var silent = args.Any(a =>
            a.Equals("--silent", StringComparison.OrdinalIgnoreCase)
            || a.Equals("/S", StringComparison.OrdinalIgnoreCase));

        if (silent)
        {
            // 静默安装没有界面，出错原因写到 %TEMP%\Vibelish-Setup.log。
            var log = Path.Combine(Path.GetTempPath(), "Vibelish-Setup.log");
            try
            {
                InstallerCore.Install(true, false, _ => { });
                File.Delete(log);
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(log, DateTime.Now.ToString("s") + " " + InstallerCore.ProductVersion + Environment.NewLine + ex);
                }
                catch
                {
                    // Ignore.
                }

                return 1;
            }
        }

        var form = new SetupForm();
        // 开发用：--preview <options|busy|error|done> [截图.png]，不安装，只显示界面。
        if (args.Length >= 2 && args[0] == "--preview")
        {
            var page = args[1];
            var shot = args.Length >= 3 ? args[2] : null;
            form.Shown += (_, _) =>
            {
                form.TopMost = true;
                form.Activate();
                form.Preview(page);
                if (shot != null)
                    CaptureLater(form, shot);
            };
        }

        Application.Run(form);
        return 0;
    }

    static void CaptureLater(Form form, string path)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 1200 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var bounds = form.Bounds;
                using var bitmap = new Bitmap(bounds.Width, bounds.Height);
                using (var g = Graphics.FromImage(bitmap))
                    g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch
            {
                // Ignore.
            }

            form.Close();
        };
        timer.Start();
    }
}
