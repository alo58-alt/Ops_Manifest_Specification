namespace CompanyOps.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is 1 or 2 &&
            string.Equals(args[0], "--verify-payload", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                InstallerEngine.VerifyPackagePayload(args.Length == 2 ? args[1] : null);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, args) =>
            MessageBox.Show(
                args.Exception.Message,
                "CompanyOps 安装失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        Application.Run(new InstallerForm());
        return 0;
    }
}
