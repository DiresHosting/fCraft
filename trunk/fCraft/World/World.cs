﻿// Copyright 2009, 2010, 2011 Matvei Stefarov <me@matvei.org>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using fCraft.MapConversion;


namespace fCraft {

    public sealed class World : IClassy {

        [Obsolete]
        public static readonly string[] BackupEnum = new[] {
            "Never", "5 Minutes", "10 Minutes", "15 Minutes", "20 Minutes",
            "30 Minutes", "45 Minutes", "1 Hour", "2 Hours", "3 Hours",
            "4 Hours", "6 Hours", "8 Hours", "12 Hours", "24 Hours"
        };

        public Map Map;
        public string Name;
        readonly SortedDictionary<int, Player> players = new SortedDictionary<int, Player>();
        public Player[] PlayerList;
        public bool IsLocked,
                    IsHidden,
                    PendingUnload,
                    IsFlushing,
                    NeverUnload;
        public SecurityController AccessSecurity = new SecurityController(),
                                  BuildSecurity = new SecurityController();

        public string LockedBy, UnlockedBy;
        public DateTime LockedDate, UnlockedDate;

        readonly object playerListLock = new object(),
                        lockLock = new object(),
                        patrolLock = new object();

        internal readonly object MapLock = new object();


        public World( string name ) {
            if( !Player.IsValidName( name ) ) throw new ArgumentException( "Incorrect world name format" );
            Name = name;
        }


        // Prepare for shutdown
        public void Shutdown() {
            if( ConfigKey.SaveOnShutdown.GetBool() ) {
                SaveMap();
            }
        }


        #region Map

        public void LoadMap() {
            lock( MapLock ) {
                if( Map != null ) return;
                try {
                    Map = MapUtility.Load( GetMapName() );
                } catch( Exception ex ) {
                    Logger.Log( "World.LoadMap: Failed to load map ({0}): {1}", LogType.Error,
                                GetMapName(), ex );
                }

                // or generate a default one
                if( Map != null ) {
                    Map.World = this;
                } else {
                    Logger.Log( "World.LoadMap: Generating default flatgrass level.", LogType.SystemActivity );
                    Map = new Map( this, 64, 64, 64, true );

                    MapGenerator.GenerateFlatgrass( Map );
                    Map.ResetSpawn();
                }

                if( OnLoaded != null ) OnLoaded();
            }
        }


        public void UnloadMap( bool doubleCheckPendingUnload ) {
            Map thisMap = Map;
            lock( MapLock ) {
                if( doubleCheckPendingUnload && !PendingUnload ) return;
                SaveMap();
                Map = null;
                PendingUnload = false;
                if( OnUnloaded != null ) OnUnloaded();
            }
            thisMap.World = null;
            thisMap.Blocks = null;
            Server.RequestGC();
        }


        public string GetMapName() {
            return Path.Combine( Paths.MapPath, Name + ".fcm" );
        }


        public void SaveMap() {
            lock( MapLock ) {
                if( Map != null ) {
                    Map.Save( GetMapName() );
                }
            }
        }


        public void ChangeMap( Map newMap ) {
            if( newMap == null ) throw new ArgumentNullException( "newMap" );
            lock( playerListLock ) {
                lock( MapLock ) {
                    Map = null;
                    World newWorld = new World( Name ) {
                        Map = newMap,
                        NeverUnload = NeverUnload,
                        AccessSecurity = { MinRank = AccessSecurity.MinRank },
                        BuildSecurity = { MinRank = BuildSecurity.MinRank }
                    };
                    newMap.World = newWorld;
                    Server.ReplaceWorld( Name, newWorld );
                    foreach( Player player in PlayerList ) {
                        SendToAll( PacketWriter.MakeRemoveEntity( player.ID ), player );
                        player.Session.JoinWorld( newWorld, null );
                    }
                }
            }
        }


        public void BeginFlushMapBuffer() {
            lock( MapLock ) {
                if( Map == null ) return;
                SendToAll( "&WMap is being flushed. Stay put, world will reload shortly." );
                IsFlushing = true;
            }
        }


        public void EndFlushMapBuffer() {
            lock( playerListLock ) {
                IsFlushing = false;
                SendToAll( "&WMap flushed. Reloading..." );
                foreach( Player player in PlayerList ) {
                    player.Session.JoinWorld( this, player.Position );
                }
            }
        }


        #endregion


        #region PlayerList

        const int MaxPlayerID = 127;

        public bool AcceptPlayer( Player player, bool announce ) {
            if( player == null ) throw new ArgumentNullException( "player" );

            lock( playerListLock ) {

                if( IsFull() ) {
                    return false;
                }

                player.ID = -1;
                for( int i = 1; i < MaxPlayerID; i++ ) {
                    if( !players.ContainsKey(i) ) {
                        player.ID = i;
                        players.Add( i, player );
                        break;
                    }
                }

                if( player.ID == -1 ) {
                    return false;
                }

                // load the map, if it's not yet loaded
                lock( MapLock ) {
                    PendingUnload = false;
                    if( Map == null ) {
                        LoadMap();
                    }

                    if( ConfigKey.BackupOnJoin.GetBool() ) {
                        string backupFileName = String.Format( "{0}_{1:yyyy-MM-dd_HH-mm}_{2}.fcm",
                                                               Name, DateTime.Now, player.Name );
                        Map.SaveBackup( Path.Combine( Paths.MapPath, GetMapName() ),
                                        Path.Combine( Paths.BackupPath, backupFileName ),
                                        true );
                    }
                }

                AddPlayerForPatrol( player );

                UpdatePlayerList();

                // Reveal newcommer to existing players
                SendToSeeing( PacketWriter.MakeAddEntity( player, player.Position ), player );

                if( announce && ConfigKey.ShowJoinedWorldMessages.GetBool() ) {
                    string message = String.Format( "&SPlayer {0}&S joined {1}", player.GetClassyName(), GetClassyName() );
                    foreach( Packet packet in PacketWriter.MakeWrappedMessage( ">", message, false ) ) {
                        Server.SendToSeeing( packet, player );
                    }
                }
            }

            Logger.Log( "Player {0} joined world {1}.", LogType.UserActivity,
                        player.Name, Name );

            if( OnPlayerJoined != null ) OnPlayerJoined( player, this );

            if( IsLocked ) {
                player.Message( "&WThis map is currently locked (read-only)." );
            }

            if( player.IsHidden ) {
                player.Message( "Reminder: You are still hidden." );
            }

            return true;
        }


        public bool ReleasePlayer( Player player ) {
            if( player == null ) throw new ArgumentNullException( "player" );
            lock( playerListLock ) {
                if( !players.Remove( player.ID ) ) {
                    return false;
                }

                RemovePlayerFromPatrol( player );

                // clear drawing status
                player.UndoBuffer.Clear();
                player.UndoBuffer.TrimExcess();
                player.SelectionMarksExpected = 0;
                player.SelectionMarks.Clear();
                player.SelectionMarkCount = 0;

                // update player list
                UpdatePlayerList();
                if( OnPlayerLeft != null ) OnPlayerLeft( player, this );
                SendToAll( PacketWriter.MakeRemoveEntity( player.ID ), player );

                // unload map (if needed)
                lock( MapLock ) {
                    if( players.Count == 0 && !NeverUnload ) {
                        PendingUnload = true;
                    }
                }
                return true;
            }
        }


        // Send a list of players to the specified new player
        internal void SendPlayerList( Player player ) {
            if( player == null ) throw new ArgumentNullException( "player" );
            Player[] tempList = PlayerList;
            for( int i = 0; i < tempList.Length; i++ ) {
                if( tempList[i] != null && tempList[i] != player && player.CanSee( tempList[i] ) ) {
                    player.Session.Send( PacketWriter.MakeAddEntity( tempList[i], tempList[i].Position ) );
                }
            }
        }


        // Find player by name using autocompletion
        public Player FindPlayer( string playerName ) {
            if( playerName == null ) throw new ArgumentNullException( "playerName" );
            Player[] tempList = PlayerList;
            Player result = null;
            for( int i = 0; i < tempList.Length; i++ ) {
                if( tempList[i] != null && tempList[i].Name.StartsWith( playerName, StringComparison.OrdinalIgnoreCase ) ) {
                    if( result == null ) {
                        result = tempList[i];
                    } else {
                        return null;
                    }
                }
            }
            return result;
        }


        public Player[] FindPlayers( Player player, string playerName ) {
            if( player == null ) throw new ArgumentNullException( "player" );
            if( playerName == null ) throw new ArgumentNullException( "playerName" );
            Player[] tempList = PlayerList;
            List<Player> results = new List<Player>();
            for( int i = 0; i < tempList.Length; i++ ) {
                if( tempList[i] != null && player.CanSee( tempList[i] ) ) {
                    if( tempList[i].Name.Equals( playerName, StringComparison.OrdinalIgnoreCase ) ) {
                        results.Clear();
                        results.Add( tempList[i] );
                        break;
                    } else if( tempList[i].Name.StartsWith( playerName, StringComparison.OrdinalIgnoreCase ) ) {
                        results.Add( tempList[i] );
                    }
                }
            }
            return results.ToArray();
        }


        /// <summary> Gets player by name (without autocompletion) </summary>
        public Player FindPlayerExact( string playerName ) {
            if( playerName == null ) throw new ArgumentNullException( "playerName" );
            Player[] tempList = PlayerList;
            for( int i = 0; i < tempList.Length; i++ ) {
                if( tempList[i] != null && tempList[i].Name.Equals( playerName, StringComparison.OrdinalIgnoreCase ) ) {
                    return tempList[i];
                }
            }
            return null;
        }


        /// <summary> Caches the player list to an array (Players -> PlayerList) </summary>
        public void UpdatePlayerList() {
            lock( playerListLock ) {
                Player[] newPlayerList = new Player[players.Count];
                int i = 0;
                foreach( Player player in players.Values ) {
                    newPlayerList[i++] = player;
                }
                PlayerList = newPlayerList;
            }
        }


        /// <summary> Counts all players (optionally includes all hidden players). </summary>
        public int CountPlayers( bool includeHiddenPlayers ) {
            if( includeHiddenPlayers ) {
                return PlayerList.Length;
            } else {
                return PlayerList.Count( player => !player.IsHidden );
            }
        }


        /// <summary> Counts only the players who are not hidden from a given observer. </summary>
        public int CountVisiblePlayers( Player observer ) {
            if( observer == null ) throw new ArgumentNullException( "observer" );
            return PlayerList.Count( observer.CanSee );
        }


        public bool IsFull() {
            return (PlayerList.Length >= ConfigKey.MaxPlayersPerWorld.GetInt());
        }


        #endregion


        #region Communication

        public void SendToAll( Packet packet ) {
            SendToAll( packet, null );
        }


        public void SendToAll( Packet packet, Player except ) {
            Player[] tempList = PlayerList;
            for( int i = 0; i < tempList.Length; i++ ) {
                if( tempList[i] != except ) {
                    tempList[i].Send( packet );
                }
            }
        }


        public void SendToAllDelayed( Packet packet, Player except ) {
            Player[] tempList = PlayerList;
            for( int i = 0; i < tempList.Length; i++ ) {
                if( tempList[i] != except ) {
                    tempList[i].SendDelayed( packet );
                }
            }
        }

        public void SendToAll( string message, params object[] args ) {
            if( message == null ) throw new ArgumentNullException( "message" );
            if( args == null ) throw new ArgumentNullException( "args" );
            if( args.Length > 0 ) message = String.Format( message, args );
            foreach( Packet p in PacketWriter.MakeWrappedMessage( "> ", message, false ) ) {
                SendToAll( p, null );
            }
        }

        public void SendToAllExcept( string message, Player except, params object[] args ) {
            if( message == null ) throw new ArgumentNullException( "message" );
            if( args == null ) throw new ArgumentNullException( "args" );
            if( args.Length > 0 ) message = String.Format( message, args );
            foreach( Packet p in PacketWriter.MakeWrappedMessage( "> ", message, false ) ) {
                SendToAll( p, except );
            }
        }


        public void SendToSeeing( Packet packet, Player source ) {
            if( source == null ) throw new ArgumentNullException( "source" );
            Player[] playerListCopy = PlayerList;
            for( int i = 0; i < playerListCopy.Length; i++ ) {
                if( playerListCopy[i] != source && playerListCopy[i].CanSee( source ) ) {
                    playerListCopy[i].Send( packet );
                }
            }
        }

        public void SendToBlind( Packet packet, Player source ) {
            if( source == null ) throw new ArgumentNullException( "source" );
            Player[] playerListCopy = PlayerList;
            for( int i = 0; i < playerListCopy.Length; i++ ) {
                if( playerListCopy[i] != source && !playerListCopy[i].CanSee( source ) ) {
                    playerListCopy[i].Send( packet );
                }
            }
        }

        #endregion


        #region Obsolete Events
        [Obsolete]
        public event SimpleEventHandler OnLoaded;
        [Obsolete]
        public event SimpleEventHandler OnUnloaded;
        [Obsolete]
        public event PlayerJoinedWorldEventHandler OnPlayerJoined;
        [Obsolete]
        public event PlayerTriedToJoinWorldEventHandler OnPlayerTriedToJoin;
        [Obsolete]
        public event PlayerLeftWorldEventHandler OnPlayerLeft;
        [Obsolete]
        public event PlayerChangedBlockEventHandler OnPlayerChangedBlock;
        [Obsolete]
        public event PlayerSentMessageEventHandler OnPlayerSentMessage;

        public bool FireChangedBlockEvent( ref BlockUpdate update ) {
            bool cancel = false;
            if( OnPlayerChangedBlock != null ) {
                OnPlayerChangedBlock( this, ref update, ref cancel );
            }
            return !cancel;
        }

        public bool FireSentMessageEvent( Player player, ref string message ) {
            bool cancel = false;
            if( OnPlayerSentMessage != null ) {
                OnPlayerSentMessage( player, this, ref message, ref cancel );
            }
            return !cancel;
        }

        public bool FirePlayerTriedToJoinEvent( Player player ) {
            bool cancel = false;
            if( OnPlayerTriedToJoin != null ) {
                OnPlayerTriedToJoin( player, this, ref cancel );
            }
            return !cancel;
        }
        #endregion


        #region Lock / Unlock

        public bool Lock( Player player ) {
            if( player == null ) throw new ArgumentNullException( "player" );
            lock( lockLock ) {
                if( IsLocked ) {
                    return false;
                } else {
                    LockedBy = player.Name;
                    LockedDate = DateTime.UtcNow;
                    IsLocked = true;
                    if( Map != null ) Map.ClearUpdateQueue();
                    SendToAll( "&WMap was locked by {0}", player.GetClassyName() );
                    Logger.Log( "World {0} was locked by {1}", LogType.UserActivity,
                                Name, player.Name );
                    return true;
                }
            }
        }


        public bool Unlock( Player player ) {
            if( player == null ) throw new ArgumentNullException( "player" );
            lock( lockLock ) {
                if( IsLocked ) {
                    UnlockedBy = player.Name;
                    UnlockedDate = DateTime.UtcNow;
                    IsLocked = false;
                    SendToAll( "&WMap was unlocked by {0}", player.GetClassyName() );
                    Logger.Log( "World \"{0}\" was unlocked by {1}", LogType.UserActivity,
                                Name, player.Name );
                    return true;
                } else {
                    return false;
                }
            }
        }

        #endregion


        #region Patrol

        readonly LinkedList<Player> patrolList = new LinkedList<Player>();
        internal static Rank RankToPatrol;

        public Player GetNextPatrolTarget() {
            lock( patrolLock ) {
                if( patrolList.Count == 0 ) {
                    return null;
                } else {
                    Player player = patrolList.First.Value;
                    patrolList.RemoveFirst();
                    patrolList.AddLast( player );
                    return player;
                }
            }
        }

        void RemovePlayerFromPatrol( Player player ) {
            if( player == null ) throw new ArgumentNullException( "player" );
            lock( patrolLock ) {
                if( patrolList.Contains( player ) ) {
                    patrolList.Remove( player );
                }
            }
        }


        void AddPlayerForPatrol( Player player ) {
            if( player == null ) throw new ArgumentNullException( "player" );
            if( player.Info.Rank <= RankToPatrol ) {
                lock( patrolLock ) {
                    patrolList.AddLast( player );
                }
            }
        }


        internal void CheckIfPlayerIsPatrollable( Player player ) {
            if( player == null ) throw new ArgumentNullException( "player" );
            lock( patrolLock ) {
                if( patrolList.Contains( player ) ) {
                    if( player.Info.Rank > RankToPatrol ) {
                        RemovePlayerFromPatrol( player );
                    }
                } else if( player.Info.Rank <= RankToPatrol ) {
                    AddPlayerForPatrol( player );
                }
            }
        }

        #endregion


        #region Scheduled Tasks

        Scheduler.Task updateTask, saveTask, backupTask;


        public void StopTasks() {
            if( updateTask != null ) {
                updateTask.Stop();
                updateTask = null;
            }
            if( saveTask != null ) {
                saveTask.Stop();
                saveTask = null;
            }
            if( backupTask != null ) {
                backupTask.Stop();
                backupTask = null;
            }
        }

        public void StartTasks() {
            updateTask = Scheduler.AddTask( UpdateTask );
            updateTask.RunForever( this,
                                   TimeSpan.FromMilliseconds( ConfigKey.TickInterval.GetInt() ),
                                   TimeSpan.Zero );

            if( ConfigKey.SaveInterval.GetInt() > 0 ) {
                saveTask = Scheduler.AddTask( SaveTask );
                saveTask.RunForever( this,
                                     TimeSpan.FromSeconds( ConfigKey.SaveInterval.GetInt() ),
                                     TimeSpan.FromSeconds( ConfigKey.SaveInterval.GetInt() ) );
            }

            if( ConfigKey.BackupInterval.GetInt() > 0 ) {
                backupTask = Scheduler.AddTask( BackupTask );
                TimeSpan interval = TimeSpan.FromMinutes( ConfigKey.BackupInterval.GetInt() );
                backupTask.RunForever( this,
                                       interval,
                                       (ConfigKey.BackupOnStartup.GetBool() ? TimeSpan.Zero : interval) );
            }
        }

        void UpdateTask( Scheduler.Task task ) {
            Map tempMap = Map;
            if( tempMap != null ) {
                tempMap.ProcessUpdates();
            }
        }

        void BackupTask( Scheduler.Task task ) {
            Map tempMap = Map;
            if( tempMap != null ) {
                tempMap.SaveBackup( Path.Combine( Paths.MapPath, GetMapName() ),
                                    Path.Combine( Paths.BackupPath, String.Format( "{0}_{1:yyyy-MM-dd_HH-mm}.fcm", Name, DateTime.Now ) ),
                                    true );
            }
        }

        void SaveTask( Scheduler.Task task ) {
            Map tempMap = Map;
            if( tempMap != null && tempMap.ChangedSinceSave ) {
                SaveMap();
            }
        }

        #endregion


        public string GetClassyName() {
            string displayedName = Name;
            if( ConfigKey.RankColorsInWorldNames.GetBool() ) {
                if( ConfigKey.RankPrefixesInChat.GetBool() ) {
                    displayedName = BuildSecurity.MinRank.Prefix + displayedName;
                }
                if( ConfigKey.RankColorsInChat.GetBool() ) {
                    if( BuildSecurity.MinRank >= AccessSecurity.MinRank ) {
                        displayedName = BuildSecurity.MinRank.Color + displayedName;
                    } else {
                        displayedName = AccessSecurity.MinRank.Color + displayedName;
                    }
                }
            }
            return displayedName;
        }


        public override string ToString() {
            return String.Format( "World({0})", Name );
        }
    }
}