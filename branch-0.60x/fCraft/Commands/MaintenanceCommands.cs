﻿// Copyright 2009, 2010, 2011 Matvei Stefarov <me@matvei.org>
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using fCraft.AutoRank;

namespace fCraft {
    /// <summary>
    /// Several yet-undocumented commands, mostly related to AutoRank.
    /// </summary>
    static class MaintenanceCommands {

        internal static void Init() {
            CommandManager.RegisterCommand( CdDumpStats );

            CommandManager.RegisterCommand( CdMassRank );
            CommandManager.RegisterCommand( CdAutoRankAll );
            CommandManager.RegisterCommand( CdSetInfo );

            CommandManager.RegisterCommand( CdReload );

            CommandManager.RegisterCommand( CdShutdown );
            CommandManager.RegisterCommand( CdRestart );

            CommandManager.RegisterCommand( CdPruneDB );

            CommandManager.RegisterCommand( CdImport );

            CommandManager.RegisterCommand( new CommandDescriptor {
                Name = "bum",
                IsHidden = true,
                Category = CommandCategory.Maintenance,
                Handler = delegate( Player player, Command cmd ) {
                    string newModeName = cmd.Next();
                    if( newModeName == null ) {
                        player.Message( "{0}: S: {1}  R: {2}  S/s: {3:0.0}  R/s: {4:0.0}",
                                        player.BandwidthUseMode,
                                        player.BytesSent,
                                        player.BytesReceived,
                                        player.BytesSentRate,
                                        player.BytesReceivedRate );
                    } else {
                        var newMode = (BandwidthUseMode)Enum.Parse( typeof( BandwidthUseMode ), newModeName, true );
                        player.BandwidthUseMode = newMode;
                        player.Info.BandwidthUseMode = newMode;
                    }
                }
            } );

            CommandManager.RegisterCommand( new CommandDescriptor {
                Name = "bdbdb",
                IsHidden = true,
                Category = CommandCategory.Maintenance,
                Handler = delegate( Player player, Command cmd ) {
                    BlockDB db = player.World.BlockDB;
                    lock( db.SyncRoot ) {
                        player.Message( "BlockDB: CAP={0} SZ={1} FI={2}",
                                        db.CacheCapacity, db.cacheSize, db.lastFlushedIndex );
                    }
                }
            } );
        }


        #region Stats

        static readonly CommandDescriptor CdDumpStats = new CommandDescriptor {
            Name = "dumpstats",
            Category = CommandCategory.Maintenance,
            IsConsoleSafe = true,
            IsHidden = true,
            Permissions = new[] { Permission.EditPlayerDB },
            Help = "Writes out a number of statistics about the server. " +
                   "Only non-banned players active in the last 30 days are counted.",
            Usage = "/dumpstats FileName",
            Handler = DumpStats
        };

        const int TopPlayersToList = 3;

        internal static void DumpStats( Player player, Command cmd ) {
            string fileName = cmd.Next();
            if( fileName == null ) {
                CdDumpStats.PrintUsage( player );
                return;
            }

            if( !Paths.Contains( Paths.WorkingPath, fileName ) ) {
                player.MessageUnsafePath();
                return;
            }

            if( Paths.IsProtectedFileName( Path.GetFileName( fileName ) ) ) {
                player.Message( "You may not use this file." );
                return;
            }

            string extension = Path.GetExtension( fileName );
            if( extension == null || !extension.Equals( ".txt", StringComparison.OrdinalIgnoreCase ) ) {
                player.Message( "Stats filename must end with .txt" );
                return;
            }

            if( File.Exists( fileName ) && !cmd.IsConfirmed ) {
                player.Confirm( cmd, "File \"{0}\" already exists. Overwrite?", Path.GetFileName( fileName ) );
                return;
            }

            if( !Paths.TestFile( "dumpstats file", fileName, false, true, false ) ) {
                player.Message( "Cannot create specified file. See log for details." );
                return;
            }

            PlayerInfo[] infos;
            using( FileStream fs = File.Create( fileName ) ) {
                using( StreamWriter writer = new StreamWriter( fs ) ) {
                    infos = PlayerDB.GetPlayerListCopy();
                    if( infos.Length == 0 ) {
                        writer.WriteLine( "(TOTAL) (0 players)" );
                        writer.WriteLine();
                    } else {
                        DumpPlayerGroupStats( writer, infos, "(TOTAL)" );
                    }

                    foreach( Rank rank in RankManager.Ranks ) {
                        infos = PlayerDB.GetPlayerListCopy( rank );
                        if( infos.Length == 0 ) {
                            writer.WriteLine( "{0}: 0 players, 0 banned, 0 inactive", rank.Name );
                            writer.WriteLine();
                        } else {
                            DumpPlayerGroupStats( writer, infos, rank.Name );
                        }
                    }
                }
            }

            player.Message( "Stats saved to \"{0}\"", Path.GetFileName( fileName ) );
        }

        static void DumpPlayerGroupStats( TextWriter writer, PlayerInfo[] infos, string groupName ) {

            RankStats stat = new RankStats();
            foreach( Rank rank2 in RankManager.Ranks ) {
                stat.PreviousRank.Add( rank2, 0 );
            }

            int totalCount = infos.Length;
            int bannedCount = infos.Count( info => info.Banned );
            int inactiveCount = infos.Count( info => info.TimeSinceLastSeen.TotalDays >= 30 );
            infos = infos.Where( info => (info.TimeSinceLastSeen.TotalDays < 30 && !info.Banned) ).ToArray();

            if( infos.Length == 0 ) {
                writer.WriteLine( "{0}: {1} players, {2} banned, {3} inactive",
                                  groupName, totalCount, bannedCount, inactiveCount );
                writer.WriteLine();
                return;
            }

            for( int i = 0; i < infos.Length; i++ ) {
                stat.TimeSinceFirstLogin += infos[i].TimeSinceFirstLogin;
                stat.TimeSinceLastLogin += infos[i].TimeSinceLastLogin;
                stat.TotalTime += infos[i].TotalTime;
                stat.BlocksBuilt += infos[i].BlocksBuilt;
                stat.BlocksDeleted += infos[i].BlocksDeleted;
                stat.BlocksDrawn += infos[i].BlocksDrawn;
                stat.TimesVisited += infos[i].TimesVisited;
                stat.MessagesWritten += infos[i].MessagesWritten;
                stat.TimesKicked += infos[i].TimesKicked;
                stat.TimesKickedOthers += infos[i].TimesKickedOthers;
                stat.TimesBannedOthers += infos[i].TimesBannedOthers;
                if( infos[i].PreviousRank != null ) stat.PreviousRank[infos[i].PreviousRank]++;
            }

            stat.BlockRatio = stat.BlocksBuilt / (double)Math.Max( stat.BlocksDeleted, 1 );
            stat.BlocksChanged = stat.BlocksDeleted + stat.BlocksBuilt;


            stat.TimeSinceFirstLoginMedian = DateTime.UtcNow.Subtract( infos.OrderByDescending( info => info.FirstLoginDate )
                                                                            .ElementAt( infos.Length / 2 ).FirstLoginDate );
            stat.TimeSinceLastLoginMedian = DateTime.UtcNow.Subtract( infos.OrderByDescending( info => info.LastLoginDate )
                                                                           .ElementAt( infos.Length / 2 ).LastLoginDate );
            stat.TotalTimeMedian = infos.OrderByDescending( info => info.TotalTime ).ElementAt( infos.Length / 2 ).TotalTime;
            stat.BlocksBuiltMedian = infos.OrderByDescending( info => info.BlocksBuilt ).ElementAt( infos.Length / 2 ).BlocksBuilt;
            stat.BlocksDeletedMedian = infos.OrderByDescending( info => info.BlocksDeleted ).ElementAt( infos.Length / 2 ).BlocksDeleted;
            stat.BlocksDrawnMedian = infos.OrderByDescending( info => info.BlocksDrawn ).ElementAt( infos.Length / 2 ).BlocksDrawn;
            PlayerInfo medianBlocksChangedPlayerInfo = infos.OrderByDescending( info => (info.BlocksDeleted + info.BlocksBuilt) ).ElementAt( infos.Length / 2 );
            stat.BlocksChangedMedian = medianBlocksChangedPlayerInfo.BlocksDeleted + medianBlocksChangedPlayerInfo.BlocksBuilt;
            PlayerInfo medianBlockRatioPlayerInfo = infos.OrderByDescending( info => (info.BlocksBuilt / (double)Math.Max( info.BlocksDeleted, 1 )) )
                                                    .ElementAt( infos.Length / 2 );
            stat.BlockRatioMedian = medianBlockRatioPlayerInfo.BlocksBuilt / (double)Math.Max( medianBlockRatioPlayerInfo.BlocksDeleted, 1 );
            stat.TimesVisitedMedian = infos.OrderByDescending( info => info.TimesVisited ).ElementAt( infos.Length / 2 ).TimesVisited;
            stat.MessagesWrittenMedian = infos.OrderByDescending( info => info.MessagesWritten ).ElementAt( infos.Length / 2 ).MessagesWritten;
            stat.TimesKickedMedian = infos.OrderByDescending( info => info.TimesKicked ).ElementAt( infos.Length / 2 ).TimesKicked;
            stat.TimesKickedOthersMedian = infos.OrderByDescending( info => info.TimesKickedOthers ).ElementAt( infos.Length / 2 ).TimesKickedOthers;
            stat.TimesBannedOthersMedian = infos.OrderByDescending( info => info.TimesBannedOthers ).ElementAt( infos.Length / 2 ).TimesBannedOthers;


            stat.TopTimeSinceFirstLogin = infos.OrderBy( info => info.FirstLoginDate ).ToArray();
            stat.TopTimeSinceLastLogin = infos.OrderBy( info => info.LastLoginDate ).ToArray();
            stat.TopTotalTime = infos.OrderByDescending( info => info.TotalTime ).ToArray();
            stat.TopBlocksBuilt = infos.OrderByDescending( info => info.BlocksBuilt ).ToArray();
            stat.TopBlocksDeleted = infos.OrderByDescending( info => info.BlocksDeleted ).ToArray();
            stat.TopBlocksDrawn = infos.OrderByDescending( info => info.BlocksDrawn ).ToArray();
            stat.TopBlocksChanged = infos.OrderByDescending( info => (info.BlocksDeleted + info.BlocksBuilt) ).ToArray();
            stat.TopBlockRatio = infos.OrderByDescending( info => (info.BlocksBuilt / (double)Math.Max( info.BlocksDeleted, 1 )) ).ToArray();
            stat.TopTimesVisited = infos.OrderByDescending( info => info.TimesVisited ).ToArray();
            stat.TopMessagesWritten = infos.OrderByDescending( info => info.MessagesWritten ).ToArray();
            stat.TopTimesKicked = infos.OrderByDescending( info => info.TimesKicked ).ToArray();
            stat.TopTimesKickedOthers = infos.OrderByDescending( info => info.TimesKickedOthers ).ToArray();
            stat.TopTimesBannedOthers = infos.OrderByDescending( info => info.TimesBannedOthers ).ToArray();


            writer.WriteLine( "{0}: {1} players, {2} banned, {3} inactive",
                              groupName, totalCount, bannedCount, inactiveCount );
            writer.WriteLine( "    TimeSinceFirstLogin: {0} mean,  {1} median,  {2} total",
                              TimeSpan.FromTicks( stat.TimeSinceFirstLogin.Ticks / infos.Length ).ToCompactString(),
                              stat.TimeSinceFirstLoginMedian.ToCompactString(),
                              stat.TimeSinceFirstLogin.ToCompactString() );
            if( infos.Count() > TopPlayersToList * 2 + 1 ) {
                foreach( PlayerInfo info in stat.TopTimeSinceFirstLogin.Take( TopPlayersToList ) ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimeSinceFirstLogin.ToCompactString(), info.Name );
                }
                writer.WriteLine( "                           ...." );
                foreach( PlayerInfo info in stat.TopTimeSinceFirstLogin.Reverse().Take( TopPlayersToList ).Reverse() ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimeSinceFirstLogin.ToCompactString(), info.Name );
                }
            } else {
                foreach( PlayerInfo info in stat.TopTimeSinceFirstLogin ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimeSinceFirstLogin.ToCompactString(), info.Name );
                }
            }
            writer.WriteLine();


            writer.WriteLine( "    TimeSinceLastLogin: {0} mean,  {1} median,  {2} total",
                              TimeSpan.FromTicks( stat.TimeSinceLastLogin.Ticks / infos.Length ).ToCompactString(),
                              stat.TimeSinceLastLoginMedian.ToCompactString(),
                              stat.TimeSinceLastLogin.ToCompactString() );
            if( infos.Count() > TopPlayersToList * 2 + 1 ) {
                foreach( PlayerInfo info in stat.TopTimeSinceLastLogin.Take( TopPlayersToList ) ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimeSinceLastLogin.ToCompactString(), info.Name );
                }
                writer.WriteLine( "                           ...." );
                foreach( PlayerInfo info in stat.TopTimeSinceLastLogin.Reverse().Take( TopPlayersToList ).Reverse() ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimeSinceLastLogin.ToCompactString(), info.Name );
                }
            } else {
                foreach( PlayerInfo info in stat.TopTimeSinceLastLogin ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimeSinceLastLogin.ToCompactString(), info.Name );
                }
            }
            writer.WriteLine();


            writer.WriteLine( "    TotalTime: {0} mean,  {1} median,  {2} total",
                              TimeSpan.FromTicks( stat.TotalTime.Ticks / infos.Length ).ToCompactString(),
                              stat.TotalTimeMedian.ToCompactString(),
                              stat.TotalTime.ToCompactString() );
            if( infos.Count() > TopPlayersToList * 2 + 1 ) {
                foreach( PlayerInfo info in stat.TopTotalTime.Take( TopPlayersToList ) ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TotalTime.ToCompactString(), info.Name );
                }
                writer.WriteLine( "                           ...." );
                foreach( PlayerInfo info in stat.TopTotalTime.Reverse().Take( TopPlayersToList ).Reverse() ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TotalTime.ToCompactString(), info.Name );
                }
            } else {
                foreach( PlayerInfo info in stat.TopTotalTime ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TotalTime.ToCompactString(), info.Name );
                }
            }
            writer.WriteLine();



            writer.WriteLine( "    BlocksBuilt: {0} mean,  {1} median,  {2} total",
                              stat.BlocksBuilt / infos.Length,
                              stat.BlocksBuiltMedian,
                              stat.BlocksBuilt );
            if( infos.Count() > TopPlayersToList * 2 + 1 ) {
                foreach( PlayerInfo info in stat.TopBlocksBuilt.Take( TopPlayersToList ) ) {
                    writer.WriteLine( "        {0,20}  {1}", info.BlocksBuilt, info.Name );
                }
                writer.WriteLine( "                           ...." );
                foreach( PlayerInfo info in stat.TopBlocksBuilt.Reverse().Take( TopPlayersToList ).Reverse() ) {
                    writer.WriteLine( "        {0,20}  {1}", info.BlocksBuilt, info.Name );
                }
            } else {
                foreach( PlayerInfo info in stat.TopBlocksBuilt ) {
                    writer.WriteLine( "        {0,20}  {1}", info.BlocksBuilt, info.Name );
                }
            }
            writer.WriteLine();


            writer.WriteLine( "    BlocksDeleted: {0} mean,  {1} median,  {2} total",
                              stat.BlocksDeleted / infos.Length,
                              stat.BlocksDeletedMedian,
                              stat.BlocksDeleted );
            if( infos.Count() > TopPlayersToList * 2 + 1 ) {
                foreach( PlayerInfo info in stat.TopBlocksDeleted.Take( TopPlayersToList ) ) {
                    writer.WriteLine( "        {0,20}  {1}", info.BlocksDeleted, info.Name );
                }
                writer.WriteLine( "                           ...." );
                foreach( PlayerInfo info in stat.TopBlocksDeleted.Reverse().Take( TopPlayersToList ).Reverse() ) {
                    writer.WriteLine( "        {0,20}  {1}", info.BlocksDeleted, info.Name );
                }
            } else {
                foreach( PlayerInfo info in stat.TopBlocksDeleted ) {
                    writer.WriteLine( "        {0,20}  {1}", info.BlocksDeleted, info.Name );
                }
            }
            writer.WriteLine();



            writer.WriteLine( "    BlocksChanged: {0} mean,  {1} median,  {2} total",
                              stat.BlocksChanged / infos.Length,
                              stat.BlocksChangedMedian,
                              stat.BlocksChanged );
            if( infos.Count() > TopPlayersToList * 2 + 1 ) {
                foreach( PlayerInfo info in stat.TopBlocksChanged.Take( TopPlayersToList ) ) {
                    writer.WriteLine( "        {0,20}  {1}", (info.BlocksDeleted + info.BlocksBuilt), info.Name );
                }
                writer.WriteLine( "                           ...." );
                foreach( PlayerInfo info in stat.TopBlocksChanged.Reverse().Take( TopPlayersToList ).Reverse() ) {
                    writer.WriteLine( "        {0,20}  {1}", (info.BlocksDeleted + info.BlocksBuilt), info.Name );
                }
            } else {
                foreach( PlayerInfo info in stat.TopBlocksChanged ) {
                    writer.WriteLine( "        {0,20}  {1}", (info.BlocksDeleted + info.BlocksBuilt), info.Name );
                }
            }
            writer.WriteLine();


            writer.WriteLine( "    BlocksDrawn: {0} mean,  {1} median,  {2} total",
                              stat.BlocksDrawn / infos.Length,
                              stat.BlocksDrawnMedian,
                              stat.BlocksDrawn );
            if( infos.Count() > TopPlayersToList * 2 + 1 ) {
                foreach( PlayerInfo info in stat.TopBlocksDrawn.Take( TopPlayersToList ) ) {
                    writer.WriteLine( "        {0,20}  {1}", info.BlocksDrawn, info.Name );
                }
                writer.WriteLine( "                           ...." );
                foreach( PlayerInfo info in stat.TopBlocksDrawn.Reverse().Take( TopPlayersToList ).Reverse() ) {
                    writer.WriteLine( "        {0,20}  {1}", info.BlocksDrawn, info.Name );
                }
            } else {
                foreach( PlayerInfo info in stat.TopBlocksDrawn ) {
                    writer.WriteLine( "        {0,20}  {1}", info.BlocksDrawn, info.Name );
                }
            }


            writer.WriteLine( "    BlockRatio: {0:0.000} mean,  {1:0.000} median",
                              stat.BlockRatio,
                              stat.BlockRatioMedian );
            if( infos.Count() > TopPlayersToList * 2 + 1 ) {
                foreach( PlayerInfo info in stat.TopBlockRatio.Take( TopPlayersToList ) ) {
                    writer.WriteLine( "        {0,20:0.000}  {1}", (info.BlocksBuilt / (double)Math.Max( info.BlocksDeleted, 1 )), info.Name );
                }
                writer.WriteLine( "                           ...." );
                foreach( PlayerInfo info in stat.TopBlockRatio.Reverse().Take( TopPlayersToList ).Reverse() ) {
                    writer.WriteLine( "        {0,20:0.000}  {1}", (info.BlocksBuilt / (double)Math.Max( info.BlocksDeleted, 1 )), info.Name );
                }
            } else {
                foreach( PlayerInfo info in stat.TopBlockRatio ) {
                    writer.WriteLine( "        {0,20:0.000}  {1}", (info.BlocksBuilt / (double)Math.Max( info.BlocksDeleted, 1 )), info.Name );
                }
            }
            writer.WriteLine();


            writer.WriteLine( "    TimesVisited: {0} mean,  {1} median,  {2} total",
                              stat.TimesVisited / infos.Length,
                              stat.TimesVisitedMedian,
                              stat.TimesVisited );
            if( infos.Count() > TopPlayersToList * 2 + 1 ) {
                foreach( PlayerInfo info in stat.TopTimesVisited.Take( TopPlayersToList ) ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimesVisited, info.Name );
                }
                writer.WriteLine( "                           ...." );
                foreach( PlayerInfo info in stat.TopTimesVisited.Reverse().Take( TopPlayersToList ).Reverse() ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimesVisited, info.Name );
                }
            } else {
                foreach( PlayerInfo info in stat.TopTimesVisited ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimesVisited, info.Name );
                }
            }
            writer.WriteLine();


            writer.WriteLine( "    MessagesWritten: {0} mean,  {1} median,  {2} total",
                              stat.MessagesWritten / infos.Length,
                              stat.MessagesWrittenMedian,
                              stat.MessagesWritten );
            if( infos.Count() > TopPlayersToList * 2 + 1 ) {
                foreach( PlayerInfo info in stat.TopMessagesWritten.Take( TopPlayersToList ) ) {
                    writer.WriteLine( "        {0,20}  {1}", info.MessagesWritten, info.Name );
                }
                writer.WriteLine( "                           ...." );
                foreach( PlayerInfo info in stat.TopMessagesWritten.Reverse().Take( TopPlayersToList ).Reverse() ) {
                    writer.WriteLine( "        {0,20}  {1}", info.MessagesWritten, info.Name );
                }
            } else {
                foreach( PlayerInfo info in stat.TopMessagesWritten ) {
                    writer.WriteLine( "        {0,20}  {1}", info.MessagesWritten, info.Name );
                }
            }
            writer.WriteLine();


            writer.WriteLine( "    TimesKicked: {0:0.0} mean,  {1} median,  {2} total",
                              stat.TimesKicked / (double)infos.Length,
                              stat.TimesKickedMedian,
                              stat.TimesKicked );
            if( infos.Count() > TopPlayersToList * 2 + 1 ) {
                foreach( PlayerInfo info in stat.TopTimesKicked.Take( TopPlayersToList ) ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimesKicked, info.Name );
                }
                writer.WriteLine( "                           ...." );
                foreach( PlayerInfo info in stat.TopTimesKicked.Reverse().Take( TopPlayersToList ).Reverse() ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimesKicked, info.Name );
                }
            } else {
                foreach( PlayerInfo info in stat.TopTimesKicked ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimesKicked, info.Name );
                }
            }
            writer.WriteLine();


            writer.WriteLine( "    TimesKickedOthers: {0:0.0} mean,  {1} median,  {2} total",
                              stat.TimesKickedOthers / (double)infos.Length,
                              stat.TimesKickedOthersMedian,
                              stat.TimesKickedOthers );
            if( infos.Count() > TopPlayersToList * 2 + 1 ) {
                foreach( PlayerInfo info in stat.TopTimesKickedOthers.Take( TopPlayersToList ) ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimesKickedOthers, info.Name );
                }
                writer.WriteLine( "                           ...." );
                foreach( PlayerInfo info in stat.TopTimesKickedOthers.Reverse().Take( TopPlayersToList ).Reverse() ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimesKickedOthers, info.Name );
                }
            } else {
                foreach( PlayerInfo info in stat.TopTimesKickedOthers ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimesKickedOthers, info.Name );
                }
            }
            writer.WriteLine();


            writer.WriteLine( "    TimesBannedOthers: {0:0.0} mean,  {1} median,  {2} total",
                              stat.TimesBannedOthers / (double)infos.Length,
                              stat.TimesBannedOthersMedian,
                              stat.TimesBannedOthers );
            if( infos.Count() > TopPlayersToList * 2 + 1 ) {
                foreach( PlayerInfo info in stat.TopTimesBannedOthers.Take( TopPlayersToList ) ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimesBannedOthers, info.Name );
                }
                writer.WriteLine( "                           ...." );
                foreach( PlayerInfo info in stat.TopTimesBannedOthers.Reverse().Take( TopPlayersToList ).Reverse() ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimesBannedOthers, info.Name );
                }
            } else {
                foreach( PlayerInfo info in stat.TopTimesBannedOthers ) {
                    writer.WriteLine( "        {0,20}  {1}", info.TimesBannedOthers, info.Name );
                }
            }
            writer.WriteLine();
        }


        sealed class RankStats {
            public TimeSpan TimeSinceFirstLogin;
            public TimeSpan TimeSinceLastLogin;
            public TimeSpan TotalTime;
            public long BlocksBuilt;
            public long BlocksDeleted;
            public long BlocksChanged;
            public long BlocksDrawn;
            public double BlockRatio;
            public long TimesVisited;
            public long MessagesWritten;
            public long TimesKicked;
            public long TimesKickedOthers;
            public long TimesBannedOthers;
            public readonly Dictionary<Rank, int> PreviousRank = new Dictionary<Rank, int>();

            public TimeSpan TimeSinceFirstLoginMedian;
            public TimeSpan TimeSinceLastLoginMedian;
            public TimeSpan TotalTimeMedian;
            public int BlocksBuiltMedian;
            public int BlocksDeletedMedian;
            public int BlocksChangedMedian;
            public long BlocksDrawnMedian;
            public double BlockRatioMedian;
            public int TimesVisitedMedian;
            public int MessagesWrittenMedian;
            public int TimesKickedMedian;
            public int TimesKickedOthersMedian;
            public int TimesBannedOthersMedian;

            public PlayerInfo[] TopTimeSinceFirstLogin;
            public PlayerInfo[] TopTimeSinceLastLogin;
            public PlayerInfo[] TopTotalTime;
            public PlayerInfo[] TopBlocksBuilt;
            public PlayerInfo[] TopBlocksDeleted;
            public PlayerInfo[] TopBlocksChanged;
            public PlayerInfo[] TopBlocksDrawn;
            public PlayerInfo[] TopBlockRatio;
            public PlayerInfo[] TopTimesVisited;
            public PlayerInfo[] TopMessagesWritten;
            public PlayerInfo[] TopTimesKicked;
            public PlayerInfo[] TopTimesKickedOthers;
            public PlayerInfo[] TopTimesBannedOthers;
        }

        #endregion


        #region AutoRank

        static readonly CommandDescriptor CdAutoRankAll = new CommandDescriptor {
            Name = "autorankall",
            Category = CommandCategory.Maintenance | CommandCategory.Moderation,
            IsConsoleSafe = true,
            IsHidden = true,
            Permissions = new[] { Permission.EditPlayerDB, Permission.Promote, Permission.Demote },
            Help = "If AutoRank is disabled, it can still be called manually using this command.",
            Usage = "/autorankall [silent] [FromRank]",
            Handler = AutoRankAll
        };

        internal static void AutoRankAll( Player player, Command cmd ) {
            bool silent = (cmd.Next() != null);
            string rankName = cmd.Next();
            Rank rank = null;
            if( rankName != null ) {
                rank = Rank.Parse( rankName );
                if( rank == null ) {
                    player.MessageNoRank( rankName );
                    return;
                }
            }

            PlayerInfo[] list;
            if( rank == null ) {
                list = PlayerDB.GetPlayerListCopy();
            } else {
                list = PlayerDB.GetPlayerListCopy( rank );
            }
            DoAutoRankAll( player, list, silent, "~AutoRankAll" );
        }

        internal static void DoAutoRankAll( Player player, PlayerInfo[] list, bool silent, string message ) {

            if( player == null ) throw new ArgumentNullException( "player" );
            if( list == null ) throw new ArgumentNullException( "list" );

            if( !AutoRankManager.HasCriteria ) {
                player.Message( "AutoRankAll: No criteria found." );
                return;
            }

            player.Message( "AutoRankAll: Evaluating {0} players...", list.Length );

            Stopwatch sw = Stopwatch.StartNew();
            int promoted = 0, demoted = 0;
            for( int i = 0; i < list.Length; i++ ) {
                Rank newRank = AutoRankManager.Check( list[i] );
                if( newRank != null ) {
                    if( newRank > list[i].Rank ) {
                        promoted++;
                    } else if( newRank < list[i].Rank ) {
                        demoted++;
                    }
                    ModerationCommands.DoChangeRank( player, list[i], newRank, message, silent, true );
                }
            }
            sw.Stop();
            player.Message( "AutoRankAll: Worked for {0}ms, {1} players promoted, {2} demoted.", sw.ElapsedMilliseconds, promoted, demoted );
        }

        #endregion


        #region MassRank

        static readonly CommandDescriptor CdMassRank = new CommandDescriptor {
            Name = "massrank",
            Category = CommandCategory.Maintenance | CommandCategory.Moderation,
            IsHidden = true,
            IsConsoleSafe = true,
            Permissions = new[] { Permission.EditPlayerDB, Permission.Promote, Permission.Demote },
            Help = "",
            Usage = "/massrank FromRank ToRank [silent]",
            Handler = MassRank
        };

        internal static void MassRank( Player player, Command cmd ) {
            string fromRankName = cmd.Next();
            string toRankName = cmd.Next();
            bool silent = (cmd.Next() != null);
            if( toRankName == null ) {
                CdMassRank.PrintUsage( player );
                return;
            }

            Rank fromRank = Rank.Parse( fromRankName );
            if( fromRank == null ) {
                player.MessageNoRank( fromRankName );
                return;
            }

            Rank toRank = Rank.Parse( toRankName );
            if( toRank == null ) {
                player.MessageNoRank( toRankName );
                return;
            }

            if( fromRank == toRank ) {
                player.Message( "Ranks must be different" );
                return;
            }

            int playerCount = PlayerDB.CountPlayersByRank( fromRank );
            string verb = (fromRank > toRank ? "demot" : "promot");


            if( !cmd.IsConfirmed ) {
                player.Confirm( cmd, "About to {0}e {1} players.", verb, playerCount );
                return;
            }

            player.Message( "MassRank: {0}ing {1} players...",
                            verb, playerCount );

            int affected = PlayerDB.MassRankChange( player, fromRank, toRank, silent );
            player.Message( "MassRank: done.", affected );
        }

        #endregion


        #region SetInfo

        static readonly CommandDescriptor CdSetInfo = new CommandDescriptor {
            Name = "setinfo",
            Category = CommandCategory.Maintenance | CommandCategory.Moderation,
            IsConsoleSafe = true,
            IsHidden = true,
            Permissions = new[] { Permission.EditPlayerDB },
            Help = "Allows direct editing of player information. Editable properties: " +
                   "TimesKicked, PreviousRank, TotalTime, RankChangeType, " +
                   "BanReason, UnbanReason, RankChangeReason, LastKickReason",
            Usage = "/setinfo PlayerName Key Value",
            Handler = SetInfo
        };

        internal static void SetInfo( Player player, Command cmd ) {
            string targetName = cmd.Next();
            string propertyName = cmd.Next();
            string valName = cmd.NextAll();

            if( targetName == null || propertyName == null ) {
                CdSetInfo.PrintUsage( player );
                return;
            }

            PlayerInfo info;
            if( !PlayerDB.FindPlayerInfo( targetName, out info ) ) {
                player.Message( "More than one player found matching \"{0}\"", targetName );
            } else if( info == null ) {
                player.MessageNoPlayer( targetName );
            } else {
                switch( propertyName.ToLower() ) {
                    case "timeskicked":
                        int oldTimesKicked = info.TimesKicked;
                        if( ValidateInt( valName, 0, 1000 ) ) {
                            info.TimesKicked = Int32.Parse( valName );
                            player.Message( "TimesKicked for {0}&S changed from {1} to {2}",
                                            info.ClassyName,
                                            oldTimesKicked,
                                            info.TimesKicked );
                        } else {
                            player.Message( "Value not in valid range (0...1000)" );
                        }
                        return;

                    case "previousrank":
                        Rank newPreviousRank = Rank.Parse( valName );
                        Rank oldPreviousRank = info.PreviousRank;
                        if( newPreviousRank != null ) {
                            info.PreviousRank = newPreviousRank;
                            player.Message( "PreviousRank for {0}&S changed from {1}&S to {2}",
                                            info.ClassyName,
                                            oldPreviousRank.ClassyName,
                                            info.PreviousRank.ClassyName );
                        } else {
                            player.MessageNoRank( valName );
                        }
                        return;

                    case "totaltime":
                        TimeSpan newTotalTime;
                        TimeSpan oldTotalTime = info.TotalTime;
                        if( TimeSpan.TryParse( valName, out newTotalTime ) ) {
                            info.TotalTime = newTotalTime;
                            player.Message( "TotalTime for {0}&S changed from {1} to {2}",
                                            info.ClassyName,
                                            oldTotalTime.ToCompactString(),
                                            info.TotalTime.ToCompactString() );
                        } else {
                            player.Message( "Could not parse time. Expected format: Days.HH:MM:SS" );
                        }
                        return;

                    case "rankchangetype":
                        RankChangeType oldType = info.RankChangeType;
                        try {
                            info.RankChangeType = (RankChangeType)Enum.Parse( typeof( RankChangeType ), valName, true );
                        } catch( ArgumentException ) {
                            player.Message( "Could not parse RankChangeType. Allowed values: {0}",
                                            String.Join( ", ", Enum.GetNames( typeof( RankChangeType ) ) ) );
                            return;
                        }
                        player.Message( "RankChangeType for {0}&S changed from {1} to {2}",
                                        info.ClassyName,
                                        oldType,
                                        info.RankChangeType );
                        return;

                    case "banreason":
                        string oldBanReason = info.BanReason;
                        info.BanReason = valName;
                        player.Message( "BanReason for {0}&S changed from \"{1}\" to \"{2}\"",
                                        info.ClassyName,
                                        oldBanReason,
                                        info.BanReason );
                        return;

                    case "unbanreason":
                        string oldUnbanReason = info.UnbanReason;
                        info.UnbanReason = valName;
                        player.Message( "UnbanReason for {0}&S changed from \"{1}\" to \"{2}\"",
                                        info.ClassyName,
                                        oldUnbanReason,
                                        info.UnbanReason );
                        return;

                    case "rankchangereason":
                        string oldRankChangeReason = info.RankChangeReason;
                        info.RankChangeReason = valName;
                        player.Message( "RankChangeReason for {0}&S changed from \"{1}\" to \"{2}\"",
                                        info.ClassyName,
                                        oldRankChangeReason,
                                        info.RankChangeReason );
                        return;

                    case "lastkickreason":
                        string oldLastKickReason = info.LastKickReason;
                        info.LastKickReason = valName;
                        player.Message( "LastKickReason for {0}&S changed from \"{1}\" to \"{2}\"",
                                        info.ClassyName,
                                        oldLastKickReason,
                                        info.LastKickReason );
                        return;

                    default:
                        player.Message( "Only the following properties are editable: " +
                                        "TimesKicked, PreviousRank, TotalTime, RankChangeType, " +
                                        "BanReason, UnbanReason, RankChangeReason, LastKickReason" );
                        return;
                }
            }
        }

        static bool ValidateInt( string stringVal, int min, int max ) {
            int val;
            if( Int32.TryParse( stringVal, out val ) ) {
                return (val >= min && val <= max);
            } else {
                return false;
            }
        }

        #endregion


        #region Reload

        static readonly CommandDescriptor CdReload = new CommandDescriptor {
            Name = "reload",
            Aliases = new[] { "configreload", "reloadconfig", "autorankreload", "reloadautorank" },
            Category = CommandCategory.Maintenance,
            Permissions = new[] { Permission.ReloadConfig },
            IsConsoleSafe = true,
            Usage = "/reload config&S or &H/reload autorank",
            Help = "Reloads a given configuration file. Note that changes to ranks " +
                   "and IRC settings still require a full restart.",
            Handler = Reload
        };

        static void Reload( Player player, Command cmd ) {
            string whatToReload = cmd.Next();
            if( whatToReload == null ) {
                CdReload.PrintUsage( player );
                return;
            }

            whatToReload = whatToReload.ToLower();

            using( LogRecorder rec = new LogRecorder() ) {
                bool success;

                switch( whatToReload ) {
                    case "config":
                        success = Config.Load( true, true );
                        break;
                    case "autorank":
                        success = AutoRankManager.Init();
                        break;

                    default:
                        CdReload.PrintUsage( player );
                        return;
                }

                if( rec.HasMessages ) {
                    foreach( string msg in rec.MessageList ) {
                        player.Message( msg );
                    }
                }

                if( success ) {
                    player.Message( "Reload: reloaded {0}.", whatToReload );
                } else {
                    player.Message( "&WReload: Error(s) occured while reloading {0}.", whatToReload );
                }
            }
        }




        #endregion


        #region Shutdown, Restart

        static readonly CommandDescriptor CdShutdown = new CommandDescriptor {
            Name = "shutdown",
            Category = CommandCategory.Maintenance,
            Permissions = new[] { Permission.ShutdownServer },
            IsConsoleSafe = true,
            Help = "Shuts down the server remotely. " +
                   "The default delay before shutdown is 5 seconds (can be changed by specifying a custom number of seconds). " +
                   "A shutdown reason or message can be specified to be shown to players. You can also cancel a shutdown-in-progress " +
                   "by calling &H/shutdown abort",
            Usage = "/shutdown [Delay] [Reason]",
            Handler = Shutdown
        };

        static void Shutdown( Player player, Command cmd ) {
            int delay;
            if( !cmd.NextInt( out delay ) ) {
                delay = 5;
                cmd.Rewind();
            }
            string reason = cmd.NextAll();

            if( reason.Equals( "abort", StringComparison.OrdinalIgnoreCase ) ) {
                if( Server.CancelShutdown() ) {
                    Logger.Log( "Shutdown aborted by {0}.", LogType.UserActivity, player.Name );
                    Server.Message( "&WShutdown aborted by {0}", player.ClassyName );
                } else {
                    player.MessageNow( "Cannot abort shutdown - too late." );
                }
                return;
            }

            Server.Message( "&WServer shutting down in {0} seconds.", delay );

            TimeSpan delayTime = TimeSpan.FromSeconds( delay );
            if( String.IsNullOrEmpty( reason ) ) {
                Logger.Log( "{0} shut down the server ({1} second delay).", LogType.UserActivity,
                            player.Name, delay );
                ShutdownParams sp = new ShutdownParams( ShutdownReason.ShuttingDown, delayTime, true, false );
                Server.Shutdown( sp, false );
            } else {
                Server.Message( "&WShutdown reason: {0}", reason );
                Logger.Log( "{0} shut down the server ({1} second delay). Reason: {2}", LogType.UserActivity,
                            player.Name, delay, reason );
                ShutdownParams sp = new ShutdownParams( ShutdownReason.ShuttingDown, delayTime, true, false, reason, player );
                Server.Shutdown( sp, false );
            }
        }



        static readonly CommandDescriptor CdRestart = new CommandDescriptor {
            Name = "restart",
            Category = CommandCategory.Maintenance,
            Permissions = new[] { Permission.ShutdownServer },
            IsConsoleSafe = true,
            Help = "Restarts the server remotely. " +
                   "The default delay before restart is 5 seconds (can be changed by specifying a custom number of seconds). " +
                   "A restart reason or message can be specified to be shown to players.",
            Usage = "/restart [Delay [Reason]]",
            Handler = Restart
        };

        static void Restart( Player player, Command cmd ) {
            TimeSpan delay = ShutdownParams.DefaultDelay;
            int delaySeconds;
            if( cmd.NextInt( out delaySeconds ) ) {
                delay = TimeSpan.FromSeconds( delaySeconds );
            } else {
                cmd.Rewind();
            }
            string reason = cmd.Next();

            Server.Message( "&WServer restarting in {0} seconds.", delay );

            if( reason == null ) {
                Logger.Log( "{0} restarted the server ({1} second delay).", LogType.UserActivity,
                            player.Name, delay );
                var sp = new ShutdownParams( ShutdownReason.Restarting, delay, true, true );
                Server.Shutdown( sp, false );
            } else {
                Logger.Log( "{0} restarted the server ({1} second delay). Reason: {2}", LogType.UserActivity,
                            player.Name, delay, reason );
                var sp = new ShutdownParams( ShutdownReason.Restarting, delay, true, true, reason, player );
                Server.Shutdown( sp, false );
            }
        }

        #endregion


        #region PruneDB

        static readonly CommandDescriptor CdPruneDB = new CommandDescriptor {
            Name = "prunedb",
            Category = CommandCategory.Maintenance,
            IsConsoleSafe = true,
            IsHidden = true,
            Permissions = new[] { Permission.EditPlayerDB },
            Help = "Removes inactive players from the player database. Use with caution.",
            Handler = PruneDB
        };

        internal static void PruneDB( Player player, Command cmd ) {
            if( !cmd.IsConfirmed ) {
                player.MessageNow( "PruneDB: Finding inactive players..." );
                player.Confirm( cmd, "Remove {0} inactive players from the database?",
                                PlayerDB.CountInactivePlayers() );
                return;
            }
            player.MessageNow( "PruneDB: Removing inactive players... (this may take a while)" );
            Scheduler.NewBackgroundTask( delegate {
                PlayerDB.RemoveInactivePlayers( player );
            } ).RunOnce();
        }

        #endregion


        #region Importing

        static readonly CommandDescriptor CdImport = new CommandDescriptor {
            Name = "import",
            Aliases = new[] { "importbans", "importranks" },
            Category = CommandCategory.Maintenance,
            IsConsoleSafe = true,
            Permissions = new[] { Permission.Import },
            Usage = "/import bans Software File&S or &H/import ranks Software File Rank",
            Help = "Imports data from formats used by other servers. " +
                   "Currently only MCSharp/MCZall files are supported.",
            Handler = Import
        };

        static void Import( Player player, Command cmd ) {
            string action = cmd.Next();
            if( action == null ) {
                CdImport.PrintUsage( player );
                return;
            }

            switch( action.ToLower() ) {
                case "bans":
                    if( !player.Can( Permission.Ban ) ) {
                        player.MessageNoAccess( Permission.Ban );
                        return;
                    }
                    ImportBans( player, cmd );
                    break;

                case "ranks":
                    if( !player.Can( Permission.Promote ) ) {
                        player.MessageNoAccess( Permission.Promote );
                        return;
                    }
                    ImportRanks( player, cmd );
                    break;

                default:
                    CdImport.PrintUsage( player );
                    break;
            }
        }


        static void ImportBans( Player player, Command cmd ) {
            string serverName = cmd.Next();
            string file = cmd.Next();

            // Make sure all parameters are specified
            if( file == null ) {
                CdImport.PrintUsage( player );
                return;
            }

            // Check if file exists
            if( !File.Exists( file ) ) {
                player.Message( "File not found: {0}", file );
                return;
            }

            string[] names;

            switch( serverName.ToLower() ) {
                case "mcsharp":
                case "mczall":
                case "mclawl":
                    try {
                        names = File.ReadAllLines( file );
                    } catch( Exception ex ) {
                        Logger.Log( "Could not open \"{0}\" to import bans: {1}", LogType.Error,
                                    file,
                                    ex );
                        return;
                    }
                    break;
                default:
                    player.Message( "fCraft does not support importing from {0}", serverName );
                    return;
            }

            if( !cmd.IsConfirmed ) {
                player.Confirm( cmd, "You are about to import {0} bans.", names.Length );
                return;
            }

            string reason = "(import from " + serverName + ")";
            foreach( string name in names ) {
                if( Player.IsValidName( name ) ) {
                    ModerationCommands.DoBan( player, name, reason, false, false, false );
                } else {
                    IPAddress ip;
                    if( Server.IsIP( name ) && IPAddress.TryParse( name, out ip ) ) {
                        ModerationCommands.DoIPBan( player, ip, reason, "", false, false );
                    } else {
                        player.Message( "Could not parse \"{0}\" as either name or IP. Skipping.", name );
                    }
                }
            }

            PlayerDB.Save();
            IPBanList.Save();
        }


        static void ImportRanks( Player player, Command cmd ) {
            string serverName = cmd.Next();
            string fileName = cmd.Next();
            string rankName = cmd.Next();
            bool silent = (cmd.Next() != null);


            // Make sure all parameters are specified
            if( rankName == null ) {
                CdImport.PrintUsage( player );
                return;
            }

            // Check if file exists
            if( !File.Exists( fileName ) ) {
                player.Message( "File not found: {0}", fileName );
                return;
            }

            Rank targetRank = Rank.Parse( rankName );
            if( targetRank == null ) {
                player.MessageNoRank( rankName );
                return;
            }

            string[] names;

            switch( serverName.ToLower() ) {
                case "mcsharp":
                case "mczall":
                case "mclawl":
                    try {
                        names = File.ReadAllLines( fileName );
                    } catch( Exception ex ) {
                        Logger.Log( "Could not open \"{0}\" to import ranks: {1}", LogType.Error,
                                    fileName,
                                    ex );
                        return;
                    }
                    break;
                default:
                    player.Message( "fCraft does not support importing from {0}", serverName );
                    return;
            }

            if( !cmd.IsConfirmed ) {
                player.Confirm( cmd, "You are about to import {0} player ranks.", names.Length );
                return;
            }

            string reason = "(import from " + serverName + ")";
            foreach( string name in names ) {
                PlayerInfo info = PlayerDB.FindPlayerInfoExact( name ) ??
                                  PlayerDB.AddFakeEntry( name, RankChangeType.Promoted );
                ModerationCommands.DoChangeRank( player, info, targetRank, reason, silent, false );
            }

            PlayerDB.Save();
        }

        #endregion
    }
}