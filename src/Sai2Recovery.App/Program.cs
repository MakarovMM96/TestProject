namespace Sai2Recovery.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "SAI2 Recovery — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new MainForm(args));
    }

    public static Icon? LoadIcon()
    {
        using var s = typeof(Program).Assembly.GetManifestResourceStream("app.ico");
        return s == null ? null : new Icon(s);
    }
}
