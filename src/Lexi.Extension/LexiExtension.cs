using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;

namespace Lexi.Extension;

/// <summary>
/// Root COM object. The GUID must match the CLSID in Package.appxmanifest (com:Class Id and CreateInstance ClassId).
/// </summary>
[Guid("EB940CBB-5F48-4AC0-8050-328A3170ABF7")]
public sealed partial class LexiExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _extensionDisposedEvent;
    private readonly LexiCommandsProvider _provider = new();

    public LexiExtension(ManualResetEvent extensionDisposedEvent)
    {
        _extensionDisposedEvent = extensionDisposedEvent;
    }

    public object? GetProvider(ProviderType providerType) => providerType switch
    {
        ProviderType.Commands => _provider,
        _ => null,
    };

    public void Dispose()
    {
        _provider.Dispose();
        _extensionDisposedEvent.Set();
    }
}
