﻿// Copyright 2009, 2010, 2011 Matvei Stefarov <me@matvei.org>
using System;

namespace fCraft.Drawing {
    public sealed class RainbowBrush : IBrushFactory, IBrush, IBrushInstance {
        public static readonly RainbowBrush Instance = new RainbowBrush();

        RainbowBrush() { }

        public string Name {
            get { return "Rainbow"; }
        }

        public string Description {
            get { return Name; }
        }

        public IBrushFactory Factory {
            get { return this; }
        }


        public IBrush MakeBrush( Player player, Command cmd ) {
            return this;
        }


        public IBrushInstance MakeInstance( Player player, Command cmd, DrawOperation state ) {
            return this;
        }

        static readonly Block[] Rainbow = new[]{
            Block.Red,
            Block.Orange,
            Block.Yellow,
            Block.Green,
            Block.Aqua,
            Block.Blue,
            Block.Violet
        };

        public string InstanceDescription {
            get { return "Rainbow"; }
        }

        public IBrush Brush {
            get { return Instance; }
        }

        public bool Begin( Player player, DrawOperation state ) {
            if( player == null ) throw new ArgumentNullException( "player" );
            if( state == null ) throw new ArgumentNullException( "state" );
            return true;
        }


        public Block NextBlock( DrawOperation state ) {
            if( state == null ) throw new ArgumentNullException( "state" );
            return Rainbow[(state.Coords.X + state.Coords.Y + state.Coords.Z) % 7];
        }


        public void End() { }
    }
}