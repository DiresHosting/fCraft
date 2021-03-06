Thanks for checking out fCraft.


== Installation ==

fCraft requires Microsoft .NET Framework 3.5 (on Windows) or Mono 2.6.4+
(on Linux / Unix / Mac OS X). There are no other depenencies.

Installation instructions:
https://sourceforge.net/apps/mediawiki/fcraft/index.php?title=Installation

Before starting the server, run ConfigTool.exe to choose your settings.
To start a server, run any ONE of the front ends (fCraftConsole or fCraftUI).


== List of Files ==

    AutoLauncher.exe - EXPERIMENTAL. Automatically launches fCraftConsole, and
                       restarts it if process dies.

      ConfigTool.exe - GUI for editing fCraft's configuration, ranks, and world
                       list. Also includes a map coverter and terrain generator.
                       If you any configuration while the server is running,
                       use /reloadconfig command. Note however that the world
                       list cannot be edited while the server is running
                       (it will get overwritten).

          fCraft.dll - Core of the server, used by all other programs.

   fCraftConsole.exe - Command-line interface for fCraft.

        fCraftUI.exe - Graphical interface for fCraft.

fCraftWinService.exe - EXPERIMENTAL. Windows service frontend for fCraft.
                       After installing the service, it can be configured
                       to start automatically, and can be controlled
                       via "Services" Windows management console.
                       Usage (from command line): fCraftWinService.exe <action>
                       Where <action> can be:
                             install    to add to list of services
                             start      to start service
                             stop       to stop
                             uninstall  to remove from list of services


== Help & Support ==

Type "/help" in game or console to get started. Type "/commands" for a
list of commands.

See www.fcraft.net for news and more documentation.

For quick help/support, join #fCraft channel on Esper.net IRC:
irc://irc.esper.net:5555/fCraft

If you like fCraft, support its developer by donating! http://donate.fcraft.net

