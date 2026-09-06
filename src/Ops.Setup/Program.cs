namespace CompanyOps.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--upgrade-unattended")
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            return UnattendedUpgradeCommand.Run(args, Console.Out, Console.Error);
        }

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

        var upgradeOnly = args.Length == 1 && args[0] == "--upgrade-only";
        if (args.Length != 0 && !upgradeOnly)
        {
            Console.Error.WriteLine("只支持 --verify-payload [目录]、--upgrade-only 或 --upgrade-unattended 参数组。");
            return 2;
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
        using var form = new InstallerForm(upgradeOnly);
        Application.Run(form);
        return form.ExitCode;
    }
}
