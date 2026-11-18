namespace StreamKit.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            var composition = AppComposition.CreateAsync().GetAwaiter().GetResult();
            try
            {
                Application.Run(new MainForm(composition));
            }
            finally
            {
                composition.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"StreamKit no pudo iniciar: {ex.Message}",
                "StreamKit",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
