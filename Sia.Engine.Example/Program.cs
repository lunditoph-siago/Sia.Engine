namespace Sia.Engine.Example;

public static class Program
{
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
}
