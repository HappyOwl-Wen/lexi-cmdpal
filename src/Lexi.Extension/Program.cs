using Lexi.Extension.Services;
using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer;
using Shmuelie.WinRTServer.CsWinRT;

namespace Lexi.Extension;

/// <summary>
/// Out-of-proc COM server entry point. Command Palette activates the class registered in
/// Package.appxmanifest with "-RegisterProcessAsComServer"; the process lives until the host disposes the extension.
/// The hosting pattern is identical to the official PowerToys ExtensionTemplate.
/// </summary>
public static class Program
{
    [MTAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test")
        {
            // Diagnostics: drive the pages in-process without Command Palette (see SelfTest).
            return SelfTest.Run(args[1..]);
        }

        if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
        {
            // Only inside Command Palette: Lexi pages then make sure the palette that shows them has the keyboard.
            PaletteFocus.Enabled = true;
            ComServer server = new();
            using ManualResetEvent extensionDisposedEvent = new(false);

            // A single extension instance is handed out every time the host asks for IExtension.
            LexiExtension extensionInstance = new(extensionDisposedEvent);
            server.RegisterClass<LexiExtension, IExtension>(() => extensionInstance);
            server.Start();

            // Exit once the host disposes the extension.
            extensionDisposedEvent.WaitOne();
            server.Stop();
            server.UnsafeDispose();
        }
        else
        {
            Console.WriteLine("Lexi is a PowerToys Command Palette extension. It is started by Command Palette, not directly.");
            Console.WriteLine("Diagnostics: Lexi.Extension.exe --self-test [report-file] [--ai] [--network] [--translation]");
        }

        return 0;
    }
}
