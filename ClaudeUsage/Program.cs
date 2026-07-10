using JPSoftworks.CommandPalette.Extensions.Toolkit;

namespace ClaudeUsage;

public static class Program
{
    [MTAThread]
    public static async Task Main(string[] args)
    {
        await ExtensionHostRunner.RunAsync(
            args,
            new ExtensionHostRunnerParameters
            {
                PublisherMoniker = "Mathias",
                ProductMoniker = "ClaudeUsage",
                ExtensionFactories = new()
                {
                    new DelegateExtensionFactory(disposed => new ClaudeUsageExtension(disposed)),
                },
            });
    }
}
