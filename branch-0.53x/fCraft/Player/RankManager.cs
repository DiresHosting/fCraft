﻿// Copyright 2009, 2010, 2011 Matvei Stefarov <me@matvei.org>
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace fCraft {
    public static class RankManager {
        public static Dictionary<string, Rank> RanksByName { get; private set; }
        public static Dictionary<string, Rank> RanksByFullName { get; private set; }
        public static Dictionary<string, Rank> RanksByID { get; private set; }
        public static Dictionary<string, string> LegacyRankMapping { get; private set; }
        public static List<Rank> Ranks { get; private set; }
        public static Rank DefaultRank, LowestRank, HighestRank;


        static RankManager() {
            Reset();
            LegacyRankMapping = new Dictionary<string, string>();
        }


        /// <summary> Clears the list of ranks. </summary>
        public static void Reset() {
            if( PlayerDB.IsLoaded ) {
                throw new InvalidOperationException( "You may not reset ranks after PlayerDB has already been loaded." );
            }
            RanksByName = new Dictionary<string, Rank>();
            RanksByFullName = new Dictionary<string, Rank>();
            RanksByID = new Dictionary<string, Rank>();
            Ranks = new List<Rank>();
        }


        /// <summary> Adds a new rank to the list. Checks for duplicates. </summary>
        public static void AddRank( Rank rank ) {
            if( rank == null ) throw new ArgumentNullException( "rank" );
            if( PlayerDB.IsLoaded ) {
                throw new InvalidOperationException( "You may not add ranks after PlayerDB has already been loaded." );
            }
            // check for duplicate rank names
            if( RanksByName.ContainsKey( rank.Name.ToLower() ) ) {
                throw new RankDefinitionException( "Duplicate definition for rank \"{0}\" (by Name) was ignored.", rank.Name );
            }

            if( RanksByID.ContainsKey( rank.ID ) ) {
                throw new RankDefinitionException( "Duplicate definition for rank \"{0}\" (by ID) was ignored.", rank.Name );
            }

            Ranks.Add( rank );
            RanksByName[rank.Name.ToLower()] = rank;
            RanksByFullName[rank.GetFullName()] = rank;
            RanksByID[rank.ID] = rank;
            RebuildIndex();
        }


        /// <summary> Parses serialized rank. Accepts either the "name" or "name#ID" format.
        /// Uses legacy rank mapping table for unrecognized ranks. Does not autocomple. </summary>
        /// <param name="name"> Full rank name </param>
        /// <returns> If name could be parsed, returns the corresponding Rank object. Otherwise returns null. </returns>
        public static Rank ParseRank( string name ) {
            if( name == null ) return null;

            if( RanksByFullName.ContainsKey( name ) ) {
                return RanksByFullName[name];
            }

            if( name.Contains( "#" ) ) {
                // new format
                string id = name.Substring( name.IndexOf( "#" ) + 1 );

                if( RanksByID.ContainsKey( id ) ) {
                    // current class
                    return RanksByID[id];

                } else {
                    // unknown class
                    int tries = 0;
                    while( LegacyRankMapping.ContainsKey( id ) ) {
                        id = LegacyRankMapping[id];
                        if( RanksByID.ContainsKey( id ) ) {
                            return RanksByID[id];
                        }
                        // avoid infinite loops due to recursive definitions
                        tries++;
                        if( tries > 100 ) {
                            throw new RankDefinitionException( "Recursive legacy rank definition" );
                        }
                    }
                    // try to fall back to name-only
                    name = name.Substring( 0, name.IndexOf( '#' ) ).ToLower();
                    return RanksByName.ContainsKey( name ) ? RanksByName[name] : null;
                }

            } else if( RanksByName.ContainsKey( name.ToLower() ) ) {
                // old format
                return RanksByName[name.ToLower()]; // LEGACY

            } else {
                // totally unknown rank
                return null;
            }
        }


        /// <summary> Parses rank name (without the ID) using autocompletion. </summary>
        /// <param name="name"> Full or partial rank name. </param>
        /// <returns> If name could be parsed, returns the corresponding Rank object. Otherwise returns null. </returns>
        public static Rank FindRank( string name ) {
            if( name == null ) return null;

            Rank result = null;
            foreach( string rankName in RanksByName.Keys ) {
                if( rankName.Equals( name, StringComparison.OrdinalIgnoreCase ) ) {
                    return RanksByName[rankName];
                }
                if( rankName.StartsWith( name, StringComparison.OrdinalIgnoreCase ) ) {
                    if( result == null ) {
                        result = RanksByName[rankName];
                    } else {
                        return null;
                    }
                }
            }
            return result;
        }


        /// <summary> Finds rank by index. Rank at index 0 is the highest. </summary>
        /// <returns> If name could be parsed, returns the corresponding Rank object. Otherwise returns null. </returns>
        public static Rank FindRank( int index ) {
            if( index < 0 || index >= Ranks.Count ) {
                return null;
            }
            return Ranks[index];
        }


        public static int GetIndex( Rank rank ) {
            return (rank == null) ? 0 : (rank.Index + 1);
        }


        public static bool DeleteRank( Rank deletedRank, Rank replacementRank ) {
            if( deletedRank == null ) throw new ArgumentNullException( "deletedRank" );
            if( replacementRank == null ) throw new ArgumentNullException( "replacementRank" );
            if( PlayerDB.IsLoaded ) {
                throw new InvalidOperationException( "You may not add ranks after PlayerDB has already been loaded." );
            }
            bool rankLimitsChanged = false;
            Ranks.Remove( deletedRank );
            RanksByName.Remove( deletedRank.Name.ToLower() );
            RanksByID.Remove( deletedRank.ID );
            RanksByFullName.Remove( deletedRank.GetFullName() );
            LegacyRankMapping.Add( deletedRank.ID, replacementRank.ID );
            foreach( Rank rank in Ranks ) {
                for( int i = 0; i < rank.PermissionLimits.Length; i++ ) {
                    if( rank.GetLimit( (Permission)i ) == deletedRank ) {
                        rank.ResetLimit( (Permission)i );
                        rankLimitsChanged = true;
                    }
                }
            }
            RebuildIndex();
            return rankLimitsChanged;
        }


        internal static void RebuildIndex() {
            if( Ranks.Count == 0 ) {
                LowestRank = null;
                HighestRank = null;
                DefaultRank = null;
                return;
            }

            // find highest/lowers ranks
            HighestRank = Ranks.First();
            LowestRank = Ranks.Last();

            // assign indices
            for( int i = 0; i < Ranks.Count; i++ ) {
                Ranks[i].Index = i;
            }

            // assign nextRankUp/nextRankDown
            if( Ranks.Count > 1 ) {
                for( int i = 0; i < Ranks.Count - 1; i++ ) {
                    Ranks[i + 1].NextRankUp = Ranks[i];
                    Ranks[i].NextRankDown = Ranks[i + 1];
                }
            } else {
                Ranks[0].NextRankUp = null;
                Ranks[0].NextRankDown = null;
            }
        }


        public static bool CanRenameRank( Rank rank, string newName ) {
            if( rank == null ) throw new ArgumentNullException( "rank" );
            if( newName == null ) throw new ArgumentNullException( "newName" );
            if( rank.Name.Equals( newName, StringComparison.OrdinalIgnoreCase ) ) {
                return true;
            } else {
                return !RanksByName.ContainsKey( newName.ToLower() );
            }
        }


        public static void RenameRank( Rank rank, string newName ) {
            if( rank == null ) throw new ArgumentNullException( "rank" );
            if( newName == null ) throw new ArgumentNullException( "newName" );
            RanksByName.Remove( rank.Name.ToLower() );
            rank.Name = newName;
            RanksByName.Add( rank.Name.ToLower(), rank );
        }


        public static bool RaiseRank( Rank rank ) {
            if( rank == null ) throw new ArgumentNullException( "rank" );
            if( rank == Ranks.First() ) {
                return false;
            }
            Rank nextRankUp = Ranks[rank.Index - 1];
            Ranks[rank.Index - 1] = rank;
            Ranks[rank.Index] = nextRankUp;
            RebuildIndex();
            return true;
        }


        public static bool LowerRank( Rank rank ) {
            if( rank == null ) throw new ArgumentNullException( "rank" );
            if( rank == Ranks.Last() ) {
                return false;
            }
            Rank nextRankDown = Ranks[rank.Index + 1];
            Ranks[rank.Index + 1] = rank;
            Ranks[rank.Index] = nextRankDown;
            RebuildIndex();
            return true;
        }


        internal static void ParsePermissionLimits() {
            foreach( Rank rank in Ranks ) {
                if( !rank.ParsePermissionLimits() ) {
                    Logger.Log( "Could not parse one of the rank-limits for kick, ban, promote, and/or demote permissions for {0}. " +
                         "Any unrecognized limits were reset to defaults.", LogType.Warning, rank.Name );
                }
            }
        }


        static readonly Random Rand = new Random();
        const string IDChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        public static string GenerateID() {
            StringBuilder id = new StringBuilder();
            for( int i = 0; i < 16; i++ ) {
                id.Append( IDChars[Rand.Next( 0, IDChars.Length )] );
            }
            return id.ToString();
        }


        internal static void SortRanksByLegacyNumericRank() {
            Ranks = Ranks.OrderBy( rank => -rank.LegacyNumericRank ).ToList();
            RebuildIndex();
        }


        /// <summary> Finds the lowest rank that has all the required permissions. </summary>
        /// <param name="permissions"> One or more permissions to check for. </param>
        /// <returns> A relevant Rank object, or null of none were found. </returns>
        public static Rank GetMinRankWithPermission( params Permission[] permissions ) {
            if( permissions == null ) throw new ArgumentNullException( "permissions" );
            for( int r = Ranks.Count - 1; r >= 0; r-- ) {
                int r1 = r;
                if( permissions.All( t => Ranks[r1].Can( t ) ) ) {
                    return Ranks[r];
                }
            }
            return null;
        }
    }
}