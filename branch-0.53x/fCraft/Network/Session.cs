﻿// Copyright 2009, 2010, 2011 Matvei Stefarov <me@matvei.org>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using fCraft.Events;
using fCraft.MapConversion;
using fCraft.AutoRank;

namespace fCraft {
    /// <summary> Represents a connection to a Minecraft client.
    /// Handles low-level interactions (e.g. networking). </summary>
    public sealed class Session {
        public Player Player;
        public DateTime LoginTime = DateTime.UtcNow;

        const string NoSmpMessage = "This server is for Minecraft Classic only.";

        // status flags
        public bool CanReceive = true,
                    CanSend = true,
                    CanQueue = true,
                    IsRegistered,
                    IsReady;


        public LeaveReason LeaveReason { get; private set; }

        Thread ioThread;

        // networking
        TcpClient client;
        BinaryReader reader;
        PacketWriter writer;
        readonly ConcurrentQueue<Packet> outputQueue = new ConcurrentQueue<Packet>(),
                                         priorityOutputQueue = new ConcurrentQueue<Packet>();

        const int SocketTimeout = 10000;

        public IPAddress IP { get; private set; }


        public Session( TcpClient tcpClient ) {
            if( tcpClient == null ) throw new ArgumentNullException( "tcpClient" );

            LeaveReason = LeaveReason.Unknown;

            client = tcpClient;
            client.SendTimeout = SocketTimeout;
            client.ReceiveTimeout = SocketTimeout;
        }


        public void Start() {
            try {
                IP = ((IPEndPoint)(client.Client.RemoteEndPoint)).Address;
                if( Server.RaiseSessionConnectingEvent( IP ) ) return;

                reader = new BinaryReader( client.GetStream() );
                writer = new PacketWriter( client.GetStream() );

                Logger.Log( "Session.Start: Incoming connection from {0}", LogType.Debug,
                            IP );

                ioThread = new Thread( IoLoop ) {
                    Name = "fCraft.Session",
                    IsBackground = true
                };
                ioThread.Start();
            } catch( Exception ex ) {
                Logger.LogAndReportCrash( "Session failed to start", "fCraft", ex, false );
                Disconnect();
            }
        }


        #region I/O Loop

        void IoLoop() {
            try {
                Server.RaiseSessionConnectedEvent( this );

                // try to log the player in, otherwise die.
                if( !LoginSequence() ) return;

                BandwidthUseMode = Player.Info.BandwidthUseMode;

                // set up some temp variables
                Packet packet = new Packet();

                const int pollInterval = 200; // 2 seconds
                int pollCounter = 0;

                const int pingInterval = 3; // 6 seconds (3 * pollInterval)
                int pingCounter = 0;

                // main i/o loop
                while( CanSend ) {
                    int packetsSent = 0;

                    // detect player disconnect
                    if( pollCounter > pollInterval ) {
                        if( !client.Connected ||
                            (client.Client.Poll( 1000, SelectMode.SelectRead ) && client.Client.Available == 0) ) {
                            if( Player != null ) {
                                Logger.Log( "Session.IoLoop: Lost connection to player {0} ({1}).", LogType.Debug, Player.Name, IP );
                            } else {
                                Logger.Log( "Session.IoLoop: Lost connection to unidentified player at {0}.", LogType.Debug, IP );
                            }
                            LeaveReason = LeaveReason.ClientQuit;
                            return;
                        }
                        if( pingCounter > pingInterval ) {
                            writer.WritePing();
                            bytesSent++;
                            pingCounter = 0;
                            MeasureBandwidthUseRates();
                        }
                        pingCounter++;
                        pollCounter = 0;
                    }
                    pollCounter++;

                    if( DateTime.UtcNow.Subtract( lastMovementUpdate ) > movementUpdateInterval ) {
                        UpdateVisibleEntities();
                        lastMovementUpdate = DateTime.UtcNow;
                    }

                    // send output to player
                    while( CanSend && packetsSent < Server.MaxSessionPacketsPerTick ) {
                        if( !priorityOutputQueue.Dequeue( ref packet ) )
                            if( !outputQueue.Dequeue( ref packet ) ) break;

                        if( Player.IsDeaf && packet.OpCode == OpCode.Message ) continue;

                        writer.Write( packet.Data );
                        bytesSent += packet.Data.Length;
                        packetsSent++;

                        if( packet.OpCode == OpCode.Kick ) {
                            writer.Flush();
                            Logger.Log( "Session.IoLoop: Kick packet delivered to {0}.", LogType.Debug,
                                        Player.Name );
                            if( LeaveReason == LeaveReason.Unknown ) LeaveReason = LeaveReason.Kick;
                            return;
                        }

                        if( DateTime.UtcNow.Subtract( lastMovementUpdate ) > movementUpdateInterval ) {
                            UpdateVisibleEntities();
                            lastMovementUpdate = DateTime.UtcNow;
                        }
                    }

                    // check if player needs to change worlds
                    if( CanSend ) {
                        lock( joinWorldLock ) {
                            if( forcedWorldToJoin != null ) {
                                while( priorityOutputQueue.Dequeue( ref packet ) ) {
                                    writer.Write( packet.Data );
                                    bytesSent += packet.Data.Length;
                                    packetsSent++;
                                    if( packet.OpCode == OpCode.Kick ) {
                                        writer.Flush();
                                        Logger.Log( "Session.IoLoop: Kick packet delivered to {0}.", LogType.Debug,
                                                    Player.Name );
                                        if( LeaveReason == LeaveReason.Unknown ) LeaveReason = LeaveReason.Kick;
                                        return;
                                    }
                                }
                                if( !JoinWorldNow( forcedWorldToJoin, false, useWorldSpawn ) ) {
                                    Logger.Log( "Session.IoLoop: Player was asked to force-join a world, but it was full.", LogType.Warning );
                                    KickNow( "World is full.", LeaveReason.ServerFull );
                                }
                                forcedWorldToJoin = null;
                            }
                        }

                        if( DateTime.UtcNow.Subtract( lastMovementUpdate ) > movementUpdateInterval ) {
                            UpdateVisibleEntities();
                            lastMovementUpdate = DateTime.UtcNow;
                        }
                    }


                    // get input from player
                    while( CanReceive && client.GetStream().DataAvailable ) {
                        byte opcode = reader.ReadByte();
                        switch( (OpCode)opcode ) {

                            case OpCode.Message:
                                if( !ProcessMessagePacket() ) return;
                                break;

                            case OpCode.Teleport:
                                ProcessMovementPacket();
                                break;

                            case OpCode.SetBlockClient:
                                ProcessSetBlockPacket();
                                break;

                            case OpCode.Ping:
                                bytesReceived++;
                                continue;

                            default:
                                Logger.Log( "Player {0} was kicked after sending an invalid opcode ({1}).", LogType.SuspiciousActivity,
                                            Player.Name, opcode );
                                KickNow( "Unknown packet opcode " + opcode,
                                         LeaveReason.InvalidOpcodeKick );
                                return;
                        }

                        if( DateTime.UtcNow.Subtract( lastMovementUpdate ) > movementUpdateInterval ) {
                            UpdateVisibleEntities();
                            lastMovementUpdate = DateTime.UtcNow;
                        }
                    }

                    Thread.Sleep( SleepDelay );
                }

            } catch( IOException ) {
                LeaveReason = LeaveReason.ClientQuit;

            } catch( SocketException ) {
                LeaveReason = LeaveReason.ClientQuit;
#if !DEBUG
            } catch( Exception ex ) {
                LeaveReason = LeaveReason.ServerError;
                Logger.LogAndReportCrash( "Error in Session.IoLoop", "fCraft", ex, false );
#endif
            } finally {
                CanQueue = false;
                CanSend = false;
                Disconnect();
            }
        }


        bool ProcessMessagePacket() {
            bytesReceived += 66;
            Player.ResetIdleTimer();
            reader.ReadByte();
            string message = ReadString();
            if( Player.ContainsIllegalChars( message ) ) {
                Logger.Log( "Player.ParseMessage: {0} attempted to write illegal characters in chat and was kicked.", LogType.SuspiciousActivity,
                            Player.Name );
                Server.SendToAll( "{0}&W was kicked for attempted hacking (0x0d).", Player.GetClassyName() );
                KickNow( "Illegal characters in chat.", LeaveReason.InvalidMessageKick );
                return false;
            } else {
                try {
                    Player.ParseMessage( message, false );
                } catch( IOException ) {
                    throw;
                } catch( SocketException ) {
                    throw;
                } catch( Exception ex ) {
                    Logger.LogAndReportCrash( "Error while parsing player's message", "fCraft", ex, false );
                    Player.MessageNow( "&WAn error occured while trying to process your message. " +
                                       "Error details have been logged. " +
                                       "It is recommended that you reconnect to the server." );
                }
            }
            return true;
        }


        void ProcessMovementPacket() {
            bytesReceived += 10;
            reader.ReadByte();
            Position newPos = new Position {
                X = IPAddress.NetworkToHostOrder( reader.ReadInt16() ),
                H = IPAddress.NetworkToHostOrder( reader.ReadInt16() ),
                Y = IPAddress.NetworkToHostOrder( reader.ReadInt16() ),
                R = reader.ReadByte(),
                L = reader.ReadByte()
            };

            Position oldPos = Player.Position;

            // calculate difference between old and new positions
            Position delta = new Position {
                X = (short)(newPos.X - oldPos.X),
                Y = (short)(newPos.Y - oldPos.Y),
                H = (short)(newPos.H - oldPos.H),
                R = (byte)Math.Abs( newPos.R - oldPos.R ),
                L = (byte)Math.Abs( newPos.L - oldPos.L )
            };

            // skip everything if player hasn't moved
            if( delta.IsZero() ) return;

            bool rotChanged = (delta.R != 0) || (delta.L != 0);

            // only reset the timer if player rotated
            // if player is just pushed around, rotation does not change (and timer should not reset)
            if( rotChanged ) Player.ResetIdleTimer();

            if( Player.Info.IsFrozen ) {
                // special handling for frozen players
                if( delta.X * delta.X + delta.Y * delta.Y > AntiSpeedMaxDistanceSquared ||
                    Math.Abs( delta.H ) > 40 ) {
                    SendNow( PacketWriter.MakeSelfTeleport( Player.Position ) );
                }
                newPos.X = Player.Position.X;
                newPos.Y = Player.Position.Y;
                newPos.H = Player.Position.H;

                // recalculate deltas
                delta.X = 0;
                delta.Y = 0;
                delta.H = 0;

            } else if( !Player.Can( Permission.UseSpeedHack ) ) {
                int distSquared = delta.X * delta.X + delta.Y * delta.Y + delta.H * delta.H;
                // speedhack detection
                if( DetectMovementPacketSpam() ) {
                    return;

                } else if( (distSquared - delta.H * delta.H > AntiSpeedMaxDistanceSquared || delta.H > AntiSpeedMaxJumpDelta) &&
                           speedHackDetectionCounter >= 0 ) {

                    if( speedHackDetectionCounter == 0 ) {
                        Player.LastValidPosition = Player.Position;
                    } else if( speedHackDetectionCounter > 1 ) {
                        DenyMovement();
                        speedHackDetectionCounter = 0;
                        return;
                    }
                    speedHackDetectionCounter++;

                } else {
                    speedHackDetectionCounter = 0;
                }
            }

            if( Server.RaisePlayerMovingEvent( Player, newPos ) ) {
                DenyMovement();
                return;
            }

            Player.Position = newPos;
            Server.RaisePlayerMovedEvent( Player, oldPos );
        }


        void ProcessSetBlockPacket() {
            bytesReceived += 9;
            if( Player.World == null || Player.World.Map == null ) return;
            Player.ResetIdleTimer();
            short x = IPAddress.NetworkToHostOrder( reader.ReadInt16() );
            short h = IPAddress.NetworkToHostOrder( reader.ReadInt16() );
            short y = IPAddress.NetworkToHostOrder( reader.ReadInt16() );
            bool mode = (reader.ReadByte() == 1);
            byte type = reader.ReadByte();

            if( type > 49 ) {
                type = MapDat.MapBlock( type );
            }

            if( Player.World.Map.InBounds( x, y, h ) ) {
                var e = new PlayerClickingEventArgs( Player, x, y, h, mode, (Block)type );
                if( Server.RaisePlayerClickingEvent( e ) ) {
                    Player.RevertBlockNow( x, y, h );
                } else {
                    Server.RaisePlayerClickedEvent( Player, x, y, h, e.Mode, e.Block );
                    Player.PlaceBlock( x, y, h, e.Mode, e.Block );
                }
            }
        }

        #endregion


        public void Disconnect() {
            Server.UnregisterSession( this );
            Server.RaiseSessionDisconnectedEvent( this, LeaveReason );

            if( IsRegistered ) {
                Server.UnregisterPlayer( this );
                Server.RaisePlayerDisconnectedEventArgs( Player, LeaveReason );
                Player = null;
            }

            if( reader != null ) {
                reader.Close();
                reader = null;
            }

            if( writer != null ) {
                writer.Close();
                writer = null;
            }

            if( client != null ) {
                client.Close();
                client = null;
            }

            ioThread = null;
        }


        bool LoginSequence() {
            byte opcode = reader.ReadByte();

            if( opcode != (byte)OpCode.Handshake ) {
                if( opcode == 2 ) {
                    // This may be someone connecting with an SMP client
                    int strLen = IPAddress.NetworkToHostOrder( reader.ReadInt16() );

                    if( strLen >= 2 && strLen <= 16 ) {
                        string smpPlayerName = Encoding.UTF8.GetString( reader.ReadBytes( strLen ) );

                        Logger.Log( "Session.LoginSequence: Player \"{0}\" tried connecting with SMP/Beta client from {1}. " +
                                    "fCraft does not support SMP/Beta.", LogType.Warning,
                                    smpPlayerName, IP );

                        // send SMP KICK packet
                        writer.Write( (byte)255 );
                        byte[] stringData = Encoding.UTF8.GetBytes( NoSmpMessage );
                        writer.Write( (short)stringData.Length );
                        writer.Write( stringData );
                        bytesSent += (1 + stringData.Length);
                        writer.Flush();

                    } else {
                        // Not SMP client (invalid player name length)
                        Logger.Log( "Session.LoginSequence: Unexpected opcode in the first packet from {0}: {1}.", LogType.Error,
                                    IP, opcode );
                        KickNow( "Unexpected handshake message - possible protocol mismatch!", LeaveReason.ProtocolViolation );
                    }
                    return false;

                } else {
                    Logger.Log( "Session.LoginSequence: Unexpected opcode in the first packet from {0}: {1}.", LogType.Error,
                                IP, opcode );
                    KickNow( "Unexpected handshake message - possible protocol mismatch!", LeaveReason.ProtocolViolation );
                    return false;
                }
            }


            // Check protocol version
            int clientProtocolVersion = reader.ReadByte();
            if( clientProtocolVersion != Config.ProtocolVersion ) {
                Logger.Log( "Session.LoginSequence: Wrong protocol version: {0}.", LogType.Error,
                            clientProtocolVersion );
                KickNow( "Incompatible protocol version!", LeaveReason.ProtocolViolation );
                return false;
            }

            string playerName = ReadString();
            string verificationCode = ReadString();
            reader.ReadByte(); // unused
            bytesReceived += 131;


            // Check name for nonstandard characters
            if( !Player.IsValidName( playerName ) ) {
                Logger.Log( "Session.LoginSequence: Unacceptible player name: {0} ({1})", LogType.SuspiciousActivity,
                            playerName, IP );
                KickNow( "Invalid characters in player name!", LeaveReason.ProtocolViolation );
                return false;
            }


            // Verify name
            Player = new Player( null, playerName, this, WorldManager.MainWorld.Map.Spawn );
            bool showVerifyNamesWarning = false;
            if( !Server.VerifyName( Player.Name, verificationCode, Server.Salt ) ) {
                NameVerificationMode nameVerificationMode = ConfigKey.VerifyNames.GetEnum<NameVerificationMode>();

                string standardMessage = String.Format( "Session.LoginSequence: Could not verify player name for {0} ({1}).",
                                                        Player.Name, IP );
                if( IP.Equals( IPAddress.Loopback ) && nameVerificationMode == NameVerificationMode.Always ) {
                    Logger.Log( "{0} Player was identified as connecting from localhost and allowed in.", LogType.SuspiciousActivity,
                                standardMessage );

                } else if( IP.IsLAN() && ConfigKey.AllowUnverifiedLAN.GetBool() ) {
                    Logger.Log( "{0} Player was identified as connecting from LAN and allowed in.", LogType.SuspiciousActivity,
                                standardMessage );
                } else if( Player.Info.TimesVisited > 1 && Player.Info.LastIP.Equals( IP ) ) {
                    switch( nameVerificationMode ) {
                        case NameVerificationMode.Always:
                            Player.Info.ProcessFailedLogin( this );
                            Logger.Log( "{0} IP matched previous records for that name. " +
                                        "Player was kicked anyway because VerifyNames is set to Always.", LogType.SuspiciousActivity,
                                        standardMessage );
                            KickNow( "Could not verify player name!", LeaveReason.UnverifiedName );
                            return false;
                        case NameVerificationMode.Balanced:
                        case NameVerificationMode.Never:
                            Logger.Log( "{0} IP matched previous records for that name. Player was allowed in.", LogType.SuspiciousActivity,
                                        standardMessage );
                            break;
                    }

                } else {
                    switch( nameVerificationMode ) {
                        case NameVerificationMode.Always:
                        case NameVerificationMode.Balanced:
                            Player.Info.ProcessFailedLogin( this );
                            Logger.Log( "{0} IP did not match. Player was kicked.", LogType.SuspiciousActivity,
                                        standardMessage );
                            KickNow( "Could not verify player name!", LeaveReason.UnverifiedName );
                            return false;
                        case NameVerificationMode.Never:
                            Logger.Log( "{0} IP did not match. " +
                                        "Player was allowed in anyway because VerifyNames is set to Never.", LogType.SuspiciousActivity,
                                        standardMessage );
                            Player.Message( "&WYour name could not be verified." );
                            showVerifyNamesWarning = true;
                            break;
                    }
                }
            }


            // Check if player is banned
            if( Player.Info.Banned ) {
                Player.Info.ProcessFailedLogin( this );
                Logger.Log( "Banned player {0} tried to log in from {1}", LogType.SuspiciousActivity,
                            Player.Name, IP );
                if( ConfigKey.ShowBannedConnectionMessages.GetBool() ) {
                    Server.SendToAllWhoCan( "&SBanned player {0}&S tried to log in from {1}", null, Permission.ViewPlayerIPs,
                                            Player.GetClassyName(), IP );
                    Server.SendToAllWhoCant( "&SBanned player {0}&S tried to log in.", null, Permission.ViewPlayerIPs,
                                            Player.GetClassyName() );
                }
                string bannedMessage = String.Format( "Banned {0} ago by {1}: {2}",
                                                      Player.Info.TimeSinceBan.ToMiniString(),
                                                      Player.Info.BannedBy,
                                                      Player.Info.BanReason );
                KickNow( bannedMessage, LeaveReason.LoginFailed );
                return false;
            }


            // Check if player's IP is banned
            IPBanInfo ipBanInfo = IPBanList.Get( IP );
            if( ipBanInfo != null ) {
                Player.Info.ProcessFailedLogin( this );
                ipBanInfo.ProcessAttempt( Player );
                if( ConfigKey.ShowBannedConnectionMessages.GetBool() ) {
                    Server.SendToAll( "{0}&S tried to log in from a banned IP.", Player.GetClassyName() );
                }
                Logger.Log( "{0} tried to log in from a banned IP.", LogType.SuspiciousActivity,
                            Player.Name );
                string bannedMessage = String.Format( "IP-banned {0} ago by {1}: {2}",
                                                      DateTime.UtcNow.Subtract( ipBanInfo.BanDate ).ToMiniString(),
                                                      ipBanInfo.BannedBy,
                                                      ipBanInfo.BanReason );
                KickNow( bannedMessage, LeaveReason.LoginFailed );
                return false;
            }


            // Check if max number of connections is reached for IP
            if( !Server.RegisterSessionAndCheckConnectionCount( this ) ) {
                Player.Info.ProcessFailedLogin( this );
                Logger.Log( "Session.LoginSequence: Denied player {0}: maximum number of connections was reached for {1}", LogType.SuspiciousActivity,
                            playerName, IP );
                KickNow( String.Format( "Max connection count reached for {0}", IP ), LeaveReason.LoginFailed );
                return false;
            }


            // Check if player is paid (if required)
            if( ConfigKey.PaidPlayersOnly.GetBool() ) {
                SendNow( PacketWriter.MakeHandshake( Player,
                                                     ConfigKey.ServerName.GetString(),
                                                     "Please wait; Checking paid status..." ) );
                writer.Flush();

                if( !Player.CheckPaidStatus( Player.Name ) ) {
                    Logger.Log( "Player {0} was kicked because their account is not paid, and PaidOnly setting is enabled.", LogType.SystemActivity,
                                Player.Name );
                    KickNow( "Paid players allowed only.", LeaveReason.LoginFailed );
                    return false;
                }
            }


            // Any additional security checks should be done right here
            if( Server.RaisePlayerConnectingEvent( Player ) ) return false;


            // ----==== beyond this point, player is considered connecting (allowed to join) ====----

            // Register player for future block updates
            if( !Server.RegisterPlayerAndCheckIfFull( this ) ) {
                Logger.Log( "Player {0} was kicked because server is full.", LogType.SystemActivity,
                            Player.Name );
                string kickMessage = String.Format( "Sorry, server is full ({0}/{1})",
                                        Server.PlayerList.Length, ConfigKey.MaxPlayers.GetInt() );
                KickNow( kickMessage, LeaveReason.ServerFull );
                return false;
            }
            Player.Info.ProcessLogin( Player );


            // ----==== Beyond this point, player is considered connected (authenticated and registered) ====----


            World startingWorld = Server.RaisePlayerConnectedEvent( Player, WorldManager.MainWorld );

            // Send server information
            SendNow( PacketWriter.MakeHandshake( Player, ConfigKey.ServerName.GetString(), ConfigKey.MOTD.GetString() ) );

            // AutoRank
            if( ConfigKey.AutoRankEnabled.GetBool() ) {
                Rank newRank = AutoRankManager.Check( Player.Info );
                if( newRank != null ) {
                    ModerationCommands.DoChangeRank( Player.Console, Player.Info, newRank, "~AutoRank", false, true );
                }
            }

            bool firstTime = (Player.Info.TimesVisited == 1);
            if( !JoinWorldNow( startingWorld, true, true ) ) {
                Logger.Log( "Failed to load main world ({0}) for connecting player {1} (from {2})", LogType.Error,
                            startingWorld.Name, Player.Name, IP );
                KickNow( "Unable to join the main world.", LeaveReason.WorldFull );
                return false;
            }


            // ==== Beyond this point, player is considered ready (has a world) ====


            if( showVerifyNamesWarning ) {
                Server.SendToAllExcept( "&WName and IP of {0}&W are unverified!", Player,
                                        Player.GetClassyName() );
            }

            // Check if other banned players logged in from this IP
            PlayerInfo[] bannedPlayerNames = PlayerDB.FindPlayers( IP, 25 ).Where( playerFromSameIP => playerFromSameIP.Banned ).ToArray();
            if( bannedPlayerNames.Length > 0 ) {
                string logString = String.Format( "&WPlayer {0}&W logged in from an IP previously used by banned players: {1}",
                                                  Player.GetClassyName(),
                                                  bannedPlayerNames.JoinToClassyString() );
                Server.SendToAll( logString );
                Logger.Log( logString, LogType.SuspiciousActivity );
            }

            // Announce join
            if( ConfigKey.ShowConnectionMessages.GetBool() ) {
                Server.SendToAllExcept( Server.MakePlayerConnectedMessage( Player, firstTime, Player.World ), Player );
            }

            // check if player is still muted
            if( Player.Info.MutedUntil > DateTime.UtcNow ) {
                int secondsLeft = (int)Player.Info.MutedUntil.Subtract( DateTime.UtcNow ).TotalSeconds;
                Player.Message( "&WYou were previously muted by {0}, {1} seconds left.",
                                Player.Info.MutedBy, secondsLeft );
                Server.SendToAllExcept( "&WPlayer {0}&W was previously muted by {1}&W, {2} seconds left.", Player,
                                        Player.GetClassyName(), Player.Info.MutedBy, secondsLeft );
            }

            // check if player is still frozen
            if( Player.Info.IsFrozen ) {
                if( Player.Info.FrozenOn != DateTime.MinValue ) {
                    Player.Message( "&WYou were previously frozen {0} ago by {1}",
                                    Player.Info.TimeSinceFrozen.ToMiniString(),
                                    Player.Info.FrozenBy );
                    Server.SendToAllExcept( "&WPlayer {0}&W was previously frozen {1} ago by {2}.", Player,
                                            Player.GetClassyName(),
                                            Player.Info.TimeSinceFrozen.ToMiniString(),
                                            Player.Info.FrozenBy );
                } else {
                    Player.Message( "&WYou were previously frozen by {0}",
                                    Player.Info.FrozenBy );
                    Server.SendToAllExcept( "&WPlayer {0}&W was previously frozen by {1}.", Player,
                                            Player.GetClassyName(),
                                            Player.Info.FrozenBy );
                }
            }

            // Welcome message
            if( File.Exists( Paths.GreetingFileName ) ) {
                string[] greetingText = File.ReadAllLines( Paths.GreetingFileName );
                foreach( string greetingLine in greetingText ) {
                    StringBuilder sb = new StringBuilder( greetingLine );
                    sb.Replace( "{SERVER_NAME}", ConfigKey.ServerName.GetString() );
                    sb.Replace( "{RANK}", Player.Info.Rank.GetClassyName() );
                    sb.Replace( "{PLAYER_NAME}", Player.GetClassyName() );
                    sb.Replace( "{TIME}", DateTime.Now.ToShortTimeString() ); // localized
                    sb.Replace( "{WORLD}", Player.World.GetClassyName() );
                    sb.Replace( "{PLAYERS}", Server.CountVisiblePlayers( Player ).ToString() );
                    sb.Replace( "{WORLDS}", WorldManager.WorldList.Length.ToString() );
                    sb.Replace( "{MOTD}", ConfigKey.MOTD.GetString() );
                    Player.Message( sb.ToString() );
                }
            } else {
                if( firstTime ) {
                    Player.Message( "Welcome to {0}", ConfigKey.ServerName.GetString() );
                } else {
                    Player.Message( "Welcome back to {0}", ConfigKey.ServerName.GetString() );
                }

                Player.Message( "Your rank is {0}&S. Type &H/help&S for help.",
                                Player.Info.Rank.GetClassyName() );
            }

            // A reminder for first-time users
            if( PlayerDB.CountTotalPlayers() == 1 && Player.Info.Rank != RankManager.HighestRank ) {
                Player.Message( "Type &H/rank {0} {1}&S in console to promote yourself",
                                Player.Name, RankManager.HighestRank.Name );
            }

            Server.RaisePlayerReadyEvent( Player );
            IsReady = true;

            return true;
        }


        #region Joining Worlds

        readonly object joinWorldLock = new object();

        World forcedWorldToJoin;
        Position postJoinPosition;
        bool useWorldSpawn;

        public void JoinWorld( World newWorld ) {
            JoinWorldInternal( newWorld, Position.Zero, true );
        }


        public void JoinWorld( World newWorld, Position position ) {
            JoinWorldInternal( newWorld, position, false );
        }


        void JoinWorldInternal( World newWorld, Position position, bool doUseWorldPosition ){
            if( newWorld == null ) throw new ArgumentNullException( "newWorld" );

            lock( joinWorldLock ) {
                useWorldSpawn = doUseWorldPosition;
                postJoinPosition = position;
                forcedWorldToJoin = newWorld;
            }
        }


        internal bool JoinWorldNow( World newWorld, bool firstTime, bool doUseWorldSpawn ) {
            if( newWorld == null ) throw new ArgumentNullException( "newWorld" );

            if( !Player.CanJoin( newWorld ) ) {
                Logger.Log( "Session.JoinWorldNow: Access limits prevented {0} from joining {1}.", LogType.Error,
                            Player.Name, newWorld.Name );
                return false;
            }

            if( !newWorld.FirePlayerTriedToJoinEvent( Player ) ) {
                Logger.Log( "Session.JoinWorldNow: FirePlayerTriedToJoinEvent prevented {0} from joining {1}", LogType.Warning,
                            Player.Name, newWorld.Name );
                return false;
            }

            World oldWorld = Player.World;

            // remove player from the old world
            if( oldWorld != null && oldWorld != newWorld ) {
                if( !oldWorld.ReleasePlayer( Player ) ) {
                    Logger.Log( "Session.JoinWorldNow: Player asked to be released from its world, " +
                                "but the world did not contain the player.", LogType.Error );
                }
            }

            ResetVisibleEntities();

            ClearBlockUpdateQueue();

            Map map;

            // try to join the new world
            if( oldWorld != newWorld ) {
                bool announce = !firstTime && (oldWorld.Name != newWorld.Name);
                map = newWorld.AcceptPlayer( Player, announce );
                if( map == null ) {
                    return false;
                }
            } else {
                map = oldWorld.EnsureMapLoaded();
            }
            Player.World = newWorld;

            // Set spawn point
            Position spawn;
            if( doUseWorldSpawn ) {
                spawn = map.Spawn;
            }else{
                spawn = postJoinPosition;
            }
            Player.Position = spawn;

            // Start sending over the level copy
            if( !firstTime ) {
                SendNow( PacketWriter.MakeHandshake( Player,
                                                     ConfigKey.ServerName.GetString(),
                                                     "Loading world " + newWorld.GetClassyName() ) );
            }

            writer.WriteLevelBegin();
            bytesSent++;

            // enable Nagle's algorithm (in case it was turned off by LowLatencyMode)
            // to avoid wasting bandwidth for map transfer
            client.NoDelay = false;

            // Fetch compressed map copy
            byte[] buffer = new byte[1024];
            int mapBytesSent = 0;
            byte[] blockData;
            using( MemoryStream stream = new MemoryStream() ) {
                map.GetCompressedCopy( stream, true );
                blockData = stream.ToArray();
            }
            Logger.Log( "Session.JoinWorldNow: Sending compressed level copy ({0} bytes) to {1}.", LogType.Debug,
                        blockData.Length, Player.Name );

            // Transfer the map copy
            while( mapBytesSent < blockData.Length ) {
                int chunkSize = blockData.Length - mapBytesSent;
                if( chunkSize > 1024 ) {
                    chunkSize = 1024;
                } else {
                    // CRC fix for ManicDigger
                    for( int i = 0; i < buffer.Length; i++ ) {
                        buffer[i] = 0;
                    }
                }
                Array.Copy( blockData, mapBytesSent, buffer, 0, chunkSize );
                byte progress = (byte)(100 * mapBytesSent / blockData.Length);

                // write in chunks of 1024 bytes or less
                writer.WriteLevelChunk( buffer, chunkSize, progress );
                bytesSent += 1028; 
                mapBytesSent += chunkSize;
            }

            // Done sending over level copy
            writer.WriteLevelEnd( map );
            bytesSent += 7;

            // Send spawn point
            writer.WriteAddEntity( 255, Player, map.Spawn );
            bytesSent += 74;
            writer.WriteTeleport( 255, spawn );
            bytesSent += 10;

            Player.Message( "Joined world {0}", newWorld.GetClassyName() );

            // Turn off Nagel's algorithm again for LowLatencyMode
            if( ConfigKey.LowLatencyMode.GetBool() ) {
                client.NoDelay = true;
            }

            Server.FireWorldChangedEvent( Player, oldWorld, newWorld );

            // Done.
            if( MonoCompat.IsMono ) {
                Server.RequestGC();
            }

            return true;
        }


        #endregion


        #region Sending

        /// <summary>
        /// Send packet to player (synchronous). Sends the packet off immediately.
        /// Should not be used from any thread other than this session's IoThread.
        /// Not thread-safe (for performance reason).
        /// </summary>
        public void SendNow( Packet packet ) {
            writer.Write( packet.Data );
            bytesSent += packet.Data.Length;
        }


        /// <summary>
        /// Send packet (asynchronous, priority queue).
        /// This is used for most packets (movement, chat, etc).
        /// </summary>
        public void Send( Packet packet ) {
            if( CanQueue ) priorityOutputQueue.Enqueue( packet );
        }


        /// <summary>
        /// Send packet (asynchronous, delayed queue).
        /// This is currently only used for block updates.
        /// </summary>
        public void SendDelayed( Packet packet ) {
            if( CanQueue ) outputQueue.Enqueue( packet );
        }

        #endregion


        string ReadString() {
            return Encoding.ASCII.GetString( reader.ReadBytes( 64 ) ).TrimEnd();
        }


        public void ClearBlockUpdateQueue() {
            Packet temp = new Packet();
            while( outputQueue.Dequeue( ref temp ) ) { }
        }


        public void ClearPriorityOutputQueue() {
            Packet tempPacket = new Packet();
            while( priorityOutputQueue.Dequeue( ref tempPacket ) ) { }
        }


        #region Kicking

        /// <summary>
        /// Kick (asynchronous). Immediately blocks all client input, but waits
        /// until client thread sends the kick packet.
        /// </summary>
        public void Kick( string message, LeaveReason leaveReason ) {
            LeaveReason = leaveReason;

            CanReceive = false;
            CanQueue = false;

            // clear all pending output to be written to client (it won't matter after the kick)
            ClearBlockUpdateQueue();
            ClearPriorityOutputQueue();

            // bypassing Send() because canQueue is false
            priorityOutputQueue.Enqueue( PacketWriter.MakeDisconnect( message ) );
        }


        /// <summary>
        /// Kick (synchronous). Immediately sends the kick packet.
        /// Can only be used from IoThread (this is not thread-safe).
        /// </summary>
        public void KickNow( string message, LeaveReason leaveReason ) {
            LeaveReason = leaveReason;

            CanQueue = false;
            CanReceive = false;
            CanSend = false;
            SendNow( PacketWriter.MakeDisconnect( message ) );
            writer.Flush();
        }


        /// <summary>
        /// Blocks the calling thread until this session disconnects.
        /// </summary>
        public void WaitForDisconnect() {
            if( ioThread != null && ioThread.IsAlive ) {
                try {
                    ioThread.Join();
                } catch( NullReferenceException ) {
                } catch( ThreadStateException ) { }
            }
        }

        #endregion


        #region Bandwidth Use Tweaks

        BandwidthUseMode bandwidthUseMode;
        int entityShowingThreshold, entityHidingThreshold;
        bool partialUpdates, skipUpdates;

        const int SleepDelay = 5;
        DateTime lastMovementUpdate;
        TimeSpan movementUpdateInterval;


        public BandwidthUseMode BandwidthUseMode {
            get {
                return bandwidthUseMode;
            }

            set {
                if( value == BandwidthUseMode.Default ) {
                    value = ConfigKey.BandwidthUseMode.GetEnum<BandwidthUseMode>();
                }
                bandwidthUseMode = value;
                switch( bandwidthUseMode ) {
                    case BandwidthUseMode.VeryLow:
                        entityShowingThreshold = (40 * 32) * (40 * 32);
                        entityHidingThreshold = (42 * 32) * (42 * 32);
                        partialUpdates = true;
                        skipUpdates = true;
                        movementUpdateInterval = TimeSpan.FromMilliseconds( 100 );
                        break;

                    case BandwidthUseMode.Low:
                        entityShowingThreshold = (50 * 32) * (50 * 32);
                        entityHidingThreshold = (52 * 32) * (52 * 32);
                        partialUpdates = true;
                        skipUpdates = true;
                        movementUpdateInterval = TimeSpan.FromMilliseconds( 50 );
                        break;

                    case BandwidthUseMode.Normal:
                        entityShowingThreshold = (68 * 32) * (68 * 32);
                        entityHidingThreshold = (70 * 32) * (70 * 32);
                        partialUpdates = true;
                        skipUpdates = false;
                        movementUpdateInterval = TimeSpan.FromMilliseconds( 50 );
                        break;

                    case BandwidthUseMode.High:
                        entityShowingThreshold = (128 * 32) * (128 * 32);
                        entityHidingThreshold = (130 * 32) * (130 * 32);
                        partialUpdates = true;
                        skipUpdates = false;
                        movementUpdateInterval = TimeSpan.FromMilliseconds( 50 );
                        break;

                    case BandwidthUseMode.VeryHigh:
                        entityShowingThreshold = int.MaxValue;
                        entityHidingThreshold = int.MaxValue;
                        partialUpdates = false;
                        skipUpdates = false;
                        movementUpdateInterval = TimeSpan.FromMilliseconds( 25 );
                        break;
                }
            }
        }

        #endregion


        #region Movement Updates

        // visible entities
        readonly Dictionary<Player, VisibleEntity> entities = new Dictionary<Player, VisibleEntity>();
        readonly Stack<Player> playersToRemove = new Stack<Player>( 127 );
        readonly Stack<sbyte> freePlayerIDs = new Stack<sbyte>( 127 );

        // movement optimization
        int fullUpdateCounter;
        public const int FullPositionUpdateIntervalDefault = 20;
        public static int FullPositionUpdateInterval = FullPositionUpdateIntervalDefault;
        const int SkipMovementThresholdSquared = 64,
                  SkipRotationThresholdSquared = 1500;

        // anti-speedhack vars
        int speedHackDetectionCounter;
        const int AntiSpeedMaxJumpDelta = 25, // 16 for normal client, 25 for WoM
                  AntiSpeedMaxDistanceSquared = 1024, // 32 * 32
                  AntiSpeedMaxPacketCount = 200,
                  AntiSpeedMaxPacketInterval = 5;

        // anti-speedhack vars: packet spam
        readonly Queue<DateTime> antiSpeedPacketLog = new Queue<DateTime>();
        DateTime antiSpeedLastNotification = DateTime.UtcNow;

        internal Player SpectatedPlayer;


        void ResetVisibleEntities() {
            foreach( var pos in entities.Values ) {
                SendNow( PacketWriter.MakeRemoveEntity( pos.Id ) );
            }
            freePlayerIDs.Clear();
            for( int i = 1; i <= sbyte.MaxValue; i++ ) {
                freePlayerIDs.Push( (sbyte)i );
            }
            playersToRemove.Clear();
            entities.Clear();
        }


        void UpdateVisibleEntities() {
            if( SpectatedPlayer != null ) {
                if( SpectatedPlayer.IsDisconnected || !Player.CanSee( SpectatedPlayer ) ) {
                    Player.Message( "Stopped spectating {0}&S (disconnected)", SpectatedPlayer.GetClassyName() );
                    SpectatedPlayer = null;
                } else {
                    Position spectatePos = SpectatedPlayer.Position;
                    World spectateWorld = SpectatedPlayer.World;
                    if( spectateWorld != Player.World ) {
                        postJoinPosition = spectatePos;
                        Player.Message( "Joined {0}&S to continue spectating {1}",
                                        spectateWorld.GetClassyName(),
                                        SpectatedPlayer.GetClassyName() );
                        JoinWorldNow( spectateWorld, false, false );
                    } else if( spectatePos != Player.Position ) {
                        SendNow( PacketWriter.MakeSelfTeleport( spectatePos ) );
                    }
                }
            }

            Player[] worldPlayerList = Player.World.PlayerList;
            Position pos = Player.Position;

            for( int i = 0; i < worldPlayerList.Length; i++ ) {
                Player otherPlayer = worldPlayerList[i];
                if( otherPlayer == Player ) continue;
                if( !Player.CanSee( otherPlayer ) ) continue;
                if( SpectatedPlayer == otherPlayer || otherPlayer.Session.SpectatedPlayer == Player ) continue;

                Position otherPos = otherPlayer.Position;
                int distance = pos.DistanceSquaredTo( otherPos );

                VisibleEntity entity;
                // if Player has a corresponding VisibleEntity
                if( entities.TryGetValue( otherPlayer, out entity ) ) {
                    entity.MarkedForRetention = true;

                    if( entity.LastKnownRank != otherPlayer.Info.Rank ) {
                        ReAddEntity( entity, otherPlayer, otherPos );
                        entity.LastKnownRank = otherPlayer.Info.Rank;

                    } else if( entity.Hidden ) {
                        if( distance < entityShowingThreshold ) {
                            ShowEntity( entity, otherPos );
                        }

                    } else {
                        if( distance > entityHidingThreshold ) {
                            HideEntity( entity );

                        } else if( entity.LastKnownPosition != otherPos ) {
                            MoveEntity( entity, otherPos );
                        }
                    }
                } else {
                    AddEntity( otherPlayer, otherPos );
                }
            }
            

            // Find entities to remove (not marked for retention).
            foreach( var pair in entities ) {
                if( pair.Value.MarkedForRetention ) {
                    pair.Value.MarkedForRetention = false;
                } else {
                    playersToRemove.Push( pair.Key );
                }
            }

            // Remove non-retained entities
            while( playersToRemove.Count > 0 ) {
                RemoveEntity( playersToRemove.Pop() );
            }

            fullUpdateCounter++;
            if( fullUpdateCounter >= FullPositionUpdateInterval ) {
                fullUpdateCounter = 0;
            }
        }


        void AddEntity( Player player, Position newPos ) {
            var pos = new VisibleEntity( newPos, freePlayerIDs.Pop(), player.Info.Rank );
            entities.Add( player, pos );
            SendNow( PacketWriter.MakeAddEntity( pos.Id, player.GetListName(), newPos ) );
        }


        void HideEntity( VisibleEntity entity ) {
            entity.Hidden = true;
            entity.LastKnownPosition = VisibleEntity.HiddenPosition;
            SendNow( PacketWriter.MakeTeleport( entity.Id, VisibleEntity.HiddenPosition ) );
        }


        void ShowEntity( VisibleEntity entity, Position newPos ) {
            entity.Hidden = false;
            entity.LastKnownPosition = newPos;
            SendNow( PacketWriter.MakeTeleport( entity.Id, newPos ) );
        }


        void ReAddEntity( VisibleEntity entity, Player player, Position newPos ) {
            SendNow( PacketWriter.MakeRemoveEntity( entity.Id ) );
            SendNow( PacketWriter.MakeAddEntity( entity.Id, player.GetListName(), newPos ) );
            entity.LastKnownPosition = newPos;
        }


        void RemoveEntity( Player player ) {
            SendNow( PacketWriter.MakeRemoveEntity( entities[player].Id ) );
            freePlayerIDs.Push( entities[player].Id );
            entities.Remove( player );
        }


        void MoveEntity( VisibleEntity entity, Position newPos ) {
            Position oldPos = entity.LastKnownPosition;

            // calculate difference between old and new positions
            Position delta = new Position {
                X = (short)(newPos.X - oldPos.X),
                Y = (short)(newPos.Y - oldPos.Y),
                H = (short)(newPos.H - oldPos.H),
                R = (byte)Math.Abs( newPos.R - oldPos.R ),
                L = (byte)Math.Abs( newPos.L - oldPos.L )
            };

            bool posChanged = (delta.X != 0) || (delta.Y != 0) || (delta.H != 0);
            bool rotChanged = (delta.R != 0) || (delta.L != 0);

            if( skipUpdates ) {
                int distSquared = delta.X * delta.X + delta.Y * delta.Y + delta.H * delta.H;
                // movement optimization
                if( distSquared < SkipMovementThresholdSquared &&
                    (delta.R * delta.R + delta.L * delta.L) < SkipRotationThresholdSquared &&
                    !entity.SkippedLastMove ) {

                    entity.SkippedLastMove = true;
                    return;
                }
                entity.SkippedLastMove = false;
            }

            Packet packet;
            // create the movement packet
            if( partialUpdates && delta.FitsIntoByte() && fullUpdateCounter < FullPositionUpdateInterval ) {
                if( posChanged && rotChanged ) {
                    // incremental position + rotation update
                    packet = PacketWriter.MakeMoveRotate( entity.Id, new Position {
                        X = delta.X,
                        Y = delta.Y,
                        H = delta.H,
                        R = newPos.R,
                        L = newPos.L
                    } );

                } else if( posChanged ) {
                    // incremental position update
                    packet = PacketWriter.MakeMove( entity.Id, delta );

                } else if( rotChanged ) {
                    // absolute rotation update
                    packet = PacketWriter.MakeRotate( entity.Id, newPos );
                } else {
                    return;
                }

            } else {
                // full (absolute position + rotation) update
                packet = PacketWriter.MakeTeleport( entity.Id, newPos );
            }

            entity.LastKnownPosition = newPos;
            SendNow( packet );
        }


        sealed class VisibleEntity {
            public static readonly Position HiddenPosition = new Position( 0, 0, short.MinValue );

            public VisibleEntity( Position newPos, sbyte newId, Rank newRank ) {
                Id = newId;
                LastKnownPosition = newPos;
                MarkedForRetention = true;
                Hidden = true;
                LastKnownRank = newRank;
            }

            public readonly sbyte Id;
            public Position LastKnownPosition;
            public Rank LastKnownRank;
            public bool Hidden;
            public bool MarkedForRetention;
            public bool SkippedLastMove;
        }


        bool DetectMovementPacketSpam() {
            if( antiSpeedPacketLog.Count >= AntiSpeedMaxPacketCount ) {
                DateTime oldestTime = antiSpeedPacketLog.Dequeue();
                double spamTimer = DateTime.UtcNow.Subtract( oldestTime ).TotalSeconds;
                if( spamTimer < AntiSpeedMaxPacketInterval ) {
                    DenyMovement();
                    return true;
                }
            }
            antiSpeedPacketLog.Enqueue( DateTime.UtcNow );
            return false;
        }


        void DenyMovement() {
            SendNow( PacketWriter.MakeSelfTeleport( Player.LastValidPosition ) );
            if( DateTime.UtcNow.Subtract( antiSpeedLastNotification ).Seconds > 1 ) {
                Player.Message( "&WYou are not allowed to speedhack." );
                antiSpeedLastNotification = DateTime.UtcNow;
            }
        }

        #endregion


        #region Bandwidth Use Metering

        int bytesSent, bytesReceived;
        DateTime lastMeasurementDate = DateTime.UtcNow;
        int lastBytesSent, lastBytesReceived;

        public int BytesSent { get { return bytesSent; } }
        public int BytesReceived { get { return bytesReceived; } }
        public double BytesSentRate { get; private set; }
        public double BytesReceivedRate { get; private set; }

        void MeasureBandwidthUseRates() {
            int sentDelta = bytesSent - lastBytesSent;
            int receivedDelta = bytesReceived - lastBytesReceived;
            TimeSpan timeDelta = DateTime.UtcNow.Subtract( lastMeasurementDate );
            BytesSentRate = sentDelta / timeDelta.TotalSeconds;
            BytesReceivedRate = receivedDelta / timeDelta.TotalSeconds;
            lastBytesSent = bytesSent;
            lastBytesReceived = bytesReceived;
            lastMeasurementDate = DateTime.UtcNow;
        }

        #endregion


        public override string ToString() {
            if( Player != null ) {
                return String.Format( "Session({0}@{1})", Player, IP );
            } else {
                return String.Format( "Session({0})", IP );
            }
        }
    }
}


#region EventArgs
namespace fCraft.Events {

    public sealed class SessionConnectingEventArgs : EventArgs {
        public SessionConnectingEventArgs( IPAddress ip ) {
            IP = ip;
        }
        public bool Cancel { get; set; }
        public IPAddress IP { get; private set; }
    }


    public sealed class SessionConnectedEventArgs : EventArgs {
        public SessionConnectedEventArgs( Session session ) {
            Session = session;
        }
        public Session Session { get; private set; }
    }


    public sealed class SessionDisconnectedEventArgs : EventArgs {
        public SessionDisconnectedEventArgs( Session session, LeaveReason leaveReason ) {
            Session = session;
            LeaveReason = leaveReason;
        }
        public Session Session { get; private set; }
        public LeaveReason LeaveReason { get; private set; }
    }

}
#endregion