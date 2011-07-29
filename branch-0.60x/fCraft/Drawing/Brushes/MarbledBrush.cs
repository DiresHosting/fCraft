﻿// Copyright 2009, 2010, 2011 Matvei Stefarov <me@matvei.org>
using System;

namespace fCraft.Drawing {
    public sealed class MarbledBrushFactory : IBrushFactory {
        public static readonly MarbledBrushFactory Instance = new MarbledBrushFactory();

        MarbledBrushFactory() { }

        public string Name {
            get { return "Marbled"; }
        }

        static Random rand = new Random();
        public static int NextSeed() {
            lock( rand ) {
                return rand.Next();
            }
        }

        public IBrush MakeBrush( Player player, Command cmd ) {
            if( player == null ) throw new ArgumentNullException( "player" );
            if( cmd == null ) throw new ArgumentNullException( "cmd" );
            Block block = cmd.NextBlock( player );
            Block altBlock = cmd.NextBlock( player );

            int seed;
            if( !cmd.NextInt( out seed ) ) seed = NextSeed();

            return new MarbledBrush( block, altBlock, seed );
        }
    }


    public sealed class MarbledBrush : AbstractPerlinNoiseBrush, IBrush {

        public MarbledBrush( Block block1, Block block2, int seed )
            : base( block1, block2, seed ) {
            Coverage = 0.5f;
            Persistence = 0.8f;
            Frequency = 0.07f;
            Octaves = 3;
        }


        public MarbledBrush( MarbledBrush other )
            : base( other ) {
        }


        #region IBrush members

        public IBrushFactory Factory {
            get { return MarbledBrushFactory.Instance; }
        }


        public string Description {
            get {
                if( Block2 != Block.Undefined ) {
                    return String.Format( "{0}({1},{2})", Factory.Name, Block1, Block2 );
                } else if( Block1 != Block.Undefined ) {
                    return String.Format( "{0}({1})", Factory.Name, Block1 );
                } else {
                    return Factory.Name;
                }
            }
        }


        public IBrushInstance MakeInstance( Player player, Command cmd, DrawOperation state ) {
            if( player == null ) throw new ArgumentNullException( "player" );
            if( cmd == null ) throw new ArgumentNullException( "cmd" );
            if( state == null ) throw new ArgumentNullException( "state" );

            if( cmd.HasNext ) {
                Block block = cmd.NextBlock( player );
                if( block == Block.Undefined ) return null;
                Block altBlock = cmd.NextBlock( player );
                Block1 = block;
                Block2 = altBlock;
                int seed;
                if( !cmd.NextInt( out seed ) ) seed = MarbledBrushFactory.NextSeed();

            } else if( Block1 == Block.Undefined ) {
                player.Message( "{0}: Please specify at least one block.", Factory.Name );
                return null;
            }

            return new MarbledBrush( this );
        }

        #endregion


        #region AbstractPerlinNoiseBrush members

        public override IBrush Brush {
            get { return this; }
        }

        public override string InstanceDescription {
            get {
                return Description;
            }
        }


        protected unsafe override void ProcessData( float[, ,] rawData, Block[, ,] data ) {
            Noise.Normalize( rawData );
            Noise.Marble( rawData );
            float threshold = Noise.FindThreshold( rawData, Coverage );
            fixed( float* rawPtr = rawData ) {
                fixed( Block* ptr = data ) {
                    for( int i = 0; i < rawData.Length; i++ ) {
                        if( rawPtr[i] < threshold ) ptr[i] = Block1;
                        else ptr[i] = Block2;
                    }
                }
            }
        }

        #endregion
    }
}