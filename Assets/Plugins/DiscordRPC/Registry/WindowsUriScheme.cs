#if NET471_OR_GREATER || NETSTANDARD2_0_OR_GREATER || NET5_0_OR_GREATER
#define HAS_RUNTIME_INFORMATION
#endif

using DiscordRPC.Logging;
using System;

namespace DiscordRPC.Registry
{
    /// <summary>
    /// Registers a URI scheme on Windows.
    /// </summary>
    public sealed class WindowsUriScheme : IRegisterUriScheme
    {
        private ILogger logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="WindowsUriScheme"/> class.
        /// </summary>
        /// <param name="logger"></param>
        public WindowsUriScheme(ILogger logger)
        {
            this.logger = logger;
        }

        // STUBBED FOR UNITY. The original bodies read and wrote the Windows registry via Microsoft.Win32.Registry,
        // which isn't in Unity's default .NET Standard 2.1 profile - it's the CS1069 "forwarded to assembly" error.
        //
        // All this code ever did was register a discord-<appid>:// URL handler so Discord's "Join Game" button could
        // launch the exe from an invite. Rich Presence itself doesn't use it at all, so stubbing it costs nothing
        // today. If invites are ever wanted, either switch API Compatibility Level to .NET Framework in Player
        // Settings and restore this file from the repo, or register the scheme in the Steam build's installer.

        /// <inheritdoc/>
        public bool Register(SchemeInfo info)
        {
            logger.Warning("URI scheme registration is stubbed out in this project - Discord invites won't launch the game. Rich Presence is unaffected.");
            return false;
        }

        /// <summary>
        /// Gets the current location of the steam client. Always null here - see the note above.
        /// </summary>
        /// <returns></returns>
        public string GetSteamLocation()
        {
            return null;
        }
    }
}
