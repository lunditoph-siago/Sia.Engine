namespace Sia.UI.Example;

public static class Program
{
#if !BROWSER
    public static int Main()
    {
        try {
            using var app = new StyleShowcaseApp();
            app.Run();
            return 0;
        }
        catch (Exception exception) {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
#else
    public static async Task<int> Main()
    {
        try {
            using var app = new StyleShowcaseApp();
            await app.RunAsync();
            return 0;
        }
        catch (Exception exception) {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
#endif
}
