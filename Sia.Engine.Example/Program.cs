namespace Sia.Engine.Example;

public static class Program
{
#if !BROWSER
    public static int Main()
    {
        try {
            using var app = new SceneExampleApp();
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
            using var app = new SceneExampleApp();
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
