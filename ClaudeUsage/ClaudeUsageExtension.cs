using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;

namespace ClaudeUsage;

[Guid("8ad3ff09-374c-4da2-82e6-3c214245cf0f")]
public sealed partial class ClaudeUsageExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _disposed;
    private readonly ClaudeUsageCommandsProvider _provider = new();

    public ClaudeUsageExtension(ManualResetEvent disposed)
    {
        _disposed = disposed;
    }

    public object? GetProvider(ProviderType providerType) =>
        providerType == ProviderType.Commands ? _provider : null;

    public void Dispose() => _disposed.Set();
}
