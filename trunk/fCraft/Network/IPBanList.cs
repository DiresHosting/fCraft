﻿// Copyright 2009, 2010 Matvei Stefarov <me@matvei.org>
using System;
using System.Collections.Generic;
using System.Net;
using System.IO;

namespace fCraft {
    public static class IPBanList {

        static SortedDictionary<string, IPBanInfo> bans = new SortedDictionary<string, IPBanInfo>();
        const string BanFile = "ipbans.txt",
                     Header = "IP,bannedBy,banDate,banReason,playerName,attempts,lastAttemptName,lastAttemptDate";
        static object locker = new object();
        public static bool isLoaded;

        internal static void Load() {
            if( File.Exists( BanFile ) ) {
                using( StreamReader reader = File.OpenText( BanFile ) ) {
                    reader.ReadLine(); // header
                    while( !reader.EndOfStream ) {
                        string[] fields = reader.ReadLine().Split( ',' );
                        if( fields.Length == IPBanInfo.fieldCount ) {
                            try {
                                IPBanInfo ban = new IPBanInfo( fields );
                                if( ban.address == IPAddress.Any || ban.address == IPAddress.None ) {
                                    Logger.Log( "IPBanList.Load: Invalid IP address skipped.", LogType.Warning );
                                } else {
                                    bans.Add( ban.address.ToString(), ban );
                                }
                            } catch( FormatException ex ) {
                                Logger.Log( "IPBanList.Load: Could not parse a record: {0}", LogType.Error, ex.Message );
                            } catch( IOException ex ) {
                                Logger.Log( "IPBanList.Load: Error while trying to read from file: {0}", LogType.Error, ex.Message );
                            }
                        }
                    }
                }
                Logger.Log( "IPBanList.Load: Done loading IP ban list ({0} records).", LogType.Debug, bans.Count );
            } else {
                Logger.LogWarning( "IPBanList.Load: No IP ban file found.", WarningLogSubtype.IPBanListWarning );
            }
            isLoaded = true;
        }


        internal static void Save() {
            Logger.Log( "IPBanList.Save: Saving IP ban list ({0} records).", LogType.Debug, bans.Count );
            string tempFile = Path.GetTempFileName();
            lock( locker ) {
                using( StreamWriter writer = File.CreateText( tempFile ) ) {
                    writer.WriteLine( Header );
                    foreach( IPBanInfo entry in bans.Values ) {
                        writer.WriteLine( entry.Serialize() );
                    }
                }
            }
            try {
                if( File.Exists( BanFile ) ) {
                    File.Replace( tempFile, BanFile, null, true );
                } else {
                    File.Move( tempFile, BanFile );
                }
            } catch( Exception ex ) {
                Logger.Log( "IPBanList.Save: An error occured while trying to save ban list file: " + ex, LogType.Error );
            }
        }


        public static bool Add( IPBanInfo ban ) {
            lock( locker ) {
                if( ban == null ) return true;
                if( !bans.ContainsKey( ban.address.ToString() ) ) {
                    bans.Add( ban.address.ToString(), ban );
                    Save();
                    return true;
                } else {
                    return false;
                }
            }
        }


        public static IPBanInfo Get( IPAddress address ) {
            lock( locker ) {
                if( bans.ContainsKey( address.ToString() ) ) {
                    return bans[address.ToString()];
                } else {
                    return null;
                }
            }
        }


        // Returns true if address was banned (and was unbanned)
        // Returns false if address was not banned (and is still not banned) or if address is null
        public static bool Remove( IPAddress address ) {
            lock( locker ) {
                if( address == null ) return false;
                if( bans.Remove( address.ToString() ) ) {
                    Save();
                    return true;
                } else {
                    return false;
                }
            }
        }

        public static int CountBans() {
            return bans.Count;
        }
    }


    public sealed class IPBanInfo {
        public const int fieldCount = 8;

        public IPAddress address;
        public string bannedBy;
        public DateTime banDate;
        public string banReason;
        public string playerName;

        public short attempts;
        public string lastAttemptName;
        public DateTime lastAttemptDate;


        public IPBanInfo( string[] fields ) {
            address = IPAddress.Parse( fields[0] );
            bannedBy = fields[1];
            banDate = DateTime.Parse( fields[2] );
            banReason = PlayerInfo.Unescape( fields[3] );
            if( fields[4] != "-" ) {
                playerName = fields[4];
            }

            attempts = Int16.Parse( fields[5] );
            lastAttemptName = fields[6];
            if( fields[7] == "-" ) lastAttemptDate = DateTime.MinValue;
            else lastAttemptDate = DateTime.Parse( fields[7] );
        }


        public IPBanInfo( IPAddress _address, string _playerName, string _bannedBy, string _banReason ) {
            address = _address;
            bannedBy = _bannedBy;
            banDate = DateTime.Now;
            if( _banReason == null ) {
                banReason = "";
            } else {
                banReason = _banReason;
            }
            playerName = _playerName;

            //attempts = 0;
            lastAttemptName = _playerName;
            lastAttemptDate = DateTime.MinValue;
        }


        public string Serialize() {
            string[] fields = new string[fieldCount];

            fields[0] = address.ToString();
            fields[1] = bannedBy;
            fields[2] = banDate.ToCompactString();
            fields[3] = PlayerInfo.Escape( banReason );
            fields[4] = playerName;
            fields[5] = attempts.ToString();
            fields[6] = lastAttemptName;
            if( lastAttemptDate == DateTime.MinValue ) fields[7] = "-";
            else fields[7] = lastAttemptDate.ToCompactString();

            return String.Join( ",", fields );
        }


        public void ProcessAttempt( Player player ) {
            attempts++;
            lastAttemptDate = DateTime.Now;
            lastAttemptName = player.name;
        }
    }
}