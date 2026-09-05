namespace Sia.Engine.Example;

public static class Program
{
#if !BROWSER
    public static int Main(string[] args)
    {
        try {
            using var app = new SceneExampleApp(ParsePipeline(args));
            app.Run();
            return 0;
        }
        catch (Exception exception) {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
#else
    public static async Task<int> Main(string[] args)
    {
        try {
            using var app = new SceneExampleApp(ParsePipeline(args));
            await app.RunAsync();
            return 0;
        }
        catch (Exception exception) {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
#endif

    private static ScenePipeline ParsePipeline(string[] args) => args switch {
        [] => ScenePipeline.Pbr,
        ["--pipeline", "pbr"] => ScenePipeline.Pbr,
        ["--pipeline", "unlit"] => ScenePipeline.Unlit,
        ["--pipeline", "normals"] => ScenePipeline.Normals,
        _ => throw new ArgumentException("Usage: --pipeline pbr|unlit|normals")
    };
}
