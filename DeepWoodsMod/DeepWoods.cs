﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Netcode;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Locations;
using StardewValley.Monsters;
using StardewValley.Network;
using StardewValley.TerrainFeatures;
using xTile;
using xTile.Dimensions;
using xTile.Layers;
using xTile.Tiles;
using static DeepWoodsMod.DeepWoodsEnterExit;
using static DeepWoodsMod.DeepWoodsGlobals;
using static DeepWoodsMod.DeepWoodsSettings;

namespace DeepWoodsMod
{
    public class DeepWoods : GameLocation
    {
        public readonly NetString parentName = new NetString();
        public readonly NetPoint parentExitLocation = new NetPoint(Point.Zero);

        public readonly NetBool hasReceivedNetworkData = new NetBool(false);

        public readonly NetInt enterDir = new NetInt(0);
        public readonly NetPoint enterLocation = new NetPoint(Point.Zero);
        public readonly NetObjectList<DeepWoodsExit> exits = new NetObjectList<DeepWoodsExit>();

        public readonly NetLong uniqueMultiplayerID = new NetLong(0);

        public readonly NetInt level = new NetInt(0);
        public readonly NetInt mapWidth = new NetInt(0);
        public readonly NetInt mapHeight = new NetInt(0);

        public readonly NetBool isLichtung = new NetBool(false);
        public readonly NetBool lichtungHasLake = new NetBool(false);
        public readonly NetPoint lichtungCenter = new NetPoint(Point.Zero);

        public readonly NetBool spawnedFromObelisk = new NetBool(false);

        public readonly NetInt spawnTime = new NetInt(0);
        public readonly NetInt abandonedByParentTime = new NetInt(2600);
        public readonly NetBool hasEverBeenVisited = new NetBool(false);

        public readonly NetInt playerCount = new NetInt(0);

        public readonly NetObjectList<ResourceClump> resourceClumps = new NetObjectList<ResourceClump>();

        // Local only
        public List<Vector2> lightSources = new List<Vector2>();
        public List<Vector2> baubles = new List<Vector2>();
        public List<WeatherDebris> weatherDebris = new List<WeatherDebris>();

        // Getters for underlying net fields
        public EnterDirection EnterDir { get { return (EnterDirection)enterDir.Value; } set { enterDir.Value = (int)value; } }
        public Location EnterLocation { get { return new Location(enterLocation.Value.X, enterLocation.Value.Y); } set { enterLocation.Value = new Point(value.X, value.Y); } }
        public DeepWoods Parent { get { return Game1.getLocationFromName(parentName.Value) as DeepWoods; } }
        public Location ParentExitLocation { get { return new Location(parentExitLocation.Value.X, parentExitLocation.Value.Y); } set { parentExitLocation.Value = new Point(value.X, value.Y); } }
        public bool HasReceivedNetworkData { get { return Game1.IsMasterGame || hasReceivedNetworkData.Value; } }

        private int seed = 0;
        public int Seed
        {
            get
            {
                if (seed == 0 && Name.Length > 0)
                {
                    if (Name == "DeepWoods")
                        seed = DeepWoodsRandom.CalculateSeed(1, EnterDirection.FROM_TOP, null);
                    else
                        seed = Int32.Parse(Name.Substring(10));
                }
                return seed;
            }
        }

        public DeepWoods()
            : base()
        {
            base.critters = new List<Critter>();
        }

        public DeepWoods(string name)
            : this()
        {
            base.name.Value = name;
        }

        public DeepWoods(DeepWoods parent, int level, EnterDirection enterDir)
            : this()
        {
            base.isOutdoors.Value = true;
            base.ignoreDebrisWeather.Value = true;
            base.ignoreOutdoorLighting.Value = true;

            this.hasReceivedNetworkData.Value = true;

            this.uniqueMultiplayerID.Value = Game1.MasterPlayer.UniqueMultiplayerID;
            this.seed = DeepWoodsRandom.CalculateSeed(level, enterDir, parent?.Seed);
            if (level == 1)
            {
                base.name.Value = "DeepWoods";
            }
            else
            {
                base.name.Value = "DeepWoods_" + this.seed;
            }
            this.parentName.Value = parent?.Name;
            this.ParentExitLocation = parent?.GetExit(EnterDirToExitDir(enterDir))?.Location ?? new Location();
            this.level.Value = level;
            DeepWoodsState.LowestLevelReached = Math.Max(DeepWoodsState.LowestLevelReached, this.level.Value - 1);
            this.EnterDir = enterDir;
            this.spawnTime.Value = Game1.timeOfDay;

            this.spawnedFromObelisk.Value = parent?.spawnedFromObelisk?.Value ?? false;

            CreateSpace();
            DetermineExits();
            updateMap();

            DeepWoodsStuffCreator.AddStuff(this, new DeepWoodsRandom(this, this.seed ^ Game1.currentGameTime.TotalGameTime.Milliseconds ^ Game1.random.Next()));
            DeepWoodsMonsters.AddMonsters(this, new DeepWoodsRandom(this, this.seed ^ Game1.currentGameTime.TotalGameTime.Milliseconds ^ Game1.random.Next()));

            if (parent == null && level > 1 && !this.HasExit(CastEnterDirToExitDir(this.EnterDir)))
            {
                this.exits.Add(new DeepWoodsExit(this, CastEnterDirToExitDir(this.EnterDir), this.EnterLocation));
            }

            if (parent != null)
            {
                ModEntry.Log("Child spawned, time: " + Game1.timeOfDay + ", name: " + this.Name + ", level: " + this.level + ", parent: " + this.parentName + ", enterDir: " + this.EnterDir + ", enterLocation: " + this.EnterLocation.X + ", " + this.EnterLocation.Y, LogLevel.Debug);
            }
        }

        public DeepWoods(int level)
            : this(null, level, EnterDirection.FROM_TOP)
        {
            this.spawnedFromObelisk.Value = true;
        }

        protected override void initNetFields()
        {
            base.initNetFields();
            this.NetFields.AddFields(parentName, parentExitLocation, hasReceivedNetworkData, enterDir, enterLocation, exits, uniqueMultiplayerID, level, mapWidth, mapHeight, isLichtung, lichtungHasLake, lichtungCenter, spawnedFromObelisk, hasEverBeenVisited, spawnTime, abandonedByParentTime, playerCount, resourceClumps);
        }

        private void DetermineExits()
        {
            if (!Game1.IsMasterGame)
                throw new ApplicationException("Illegal call to DeepWoods.DetermineExits() in client.");

            this.exits.Clear();
            List<ExitDirection> possibleExitDirs = AllExitDirsBut(CastEnterDirToExitDir(this.EnterDir));
            int numExitDirs = Game1.random.Next(1, 4);
            if (numExitDirs < 3)
            {
                possibleExitDirs.RemoveAt(Game1.random.Next(0, possibleExitDirs.Count));
                if (numExitDirs < 2)
                {
                    possibleExitDirs.RemoveAt(Game1.random.Next(0, possibleExitDirs.Count));
                }
            }
            foreach (ExitDirection exitDir in possibleExitDirs)
            {
                this.exits.Add(
                    new DeepWoodsExit(
                        this,
                        exitDir,
                        new DeepWoodsSpaceManager(this.mapWidth.Value, this.mapHeight.Value).GetRandomExitLocation(exitDir, new DeepWoodsRandom(this, this.seed ^ Game1.currentGameTime.TotalGameTime.Milliseconds ^ Game1.random.Next()))
                    )
                );
            }
        }

        private void CreateSpace()
        {
            if (!Game1.IsMasterGame)
                throw new ApplicationException("Illegal call to DeepWoods.CreateSpace in client.");

            var random = new DeepWoodsRandom(this, this.Seed ^ Game1.currentGameTime.TotalGameTime.Milliseconds ^ Game1.random.Next());

            this.isLichtung.Value = this.level.Value >= Settings.Level.MinLevelForClearing && !(this.Parent?.isLichtung ?? true) && random.CheckChance(Settings.Luck.Clearings.ChanceForClearing);

            if (this.isLichtung)
            {
                this.mapWidth.Value = Game1.random.Next(Settings.Map.MinMapWidth, Settings.Map.MaxMapWidthForClearing);
                this.mapHeight.Value = Game1.random.Next(Settings.Map.MinMapWidth, Settings.Map.MaxMapWidthForClearing);
                this.lichtungHasLake.Value = random.GetRandomValue(Settings.Luck.Clearings.Perks) == LichtungStuff.Lake;
            }
            else
            {
                this.mapWidth.Value = Game1.random.Next(Settings.Map.MinMapWidth, Settings.Map.MaxMapWidth);
                this.mapHeight.Value = Game1.random.Next(Settings.Map.MinMapHeight, Settings.Map.MaxMapHeight);
            }

            this.EnterLocation = this.level == 1 ? Settings.Map.RootLevelEnterLocation : new DeepWoodsSpaceManager(this.mapWidth.Value, this.mapHeight.Value).GetRandomEnterLocation(this.EnterDir, random);
        }

        public void RemovePlayer(Farmer who)
        {
            if (DeepWoodsManager.currentDeepWoods == this)
                DeepWoodsManager.currentDeepWoods = null;

            if (!Game1.IsMasterGame)
                return;

            this.playerCount.Value = this.playerCount.Value - 1;
        }

        public void FixPlayerPosAfterWarp(Farmer who)
        {
            if (who != Game1.player)
                return;

            // First check for current warp request (stored globally for local player):
            if (DeepWoodsManager.currentWarpRequestName == this.Name
                && DeepWoodsManager.currentWarpRequestLocation.HasValue)
            {
                who.Position = DeepWoodsManager.currentWarpRequestLocation.Value;
                DeepWoodsManager.currentWarpRequestName = null;
                DeepWoodsManager.currentWarpRequestLocation = null;
            }
            else
            {
                // If no current warp request is known, we will heuristically determine the nearest valid location:
                Vector2 nearestEnterLocation = new Vector2(EnterLocation.X * 64, EnterLocation.Y * 64);
                float nearestEnterLocationDistance = (nearestEnterLocation - who.Position).Length();
                int faceDirection = EnterDirToFacingDirection(this.EnterDir);
                foreach (var exit in this.exits)
                {
                    Vector2 exitLocation = new Vector2(exit.Location.X * 64, exit.Location.Y * 64);
                    float exitDistance = (exitLocation - who.Position).Length();
                    if (exitDistance < nearestEnterLocationDistance)
                    {
                        nearestEnterLocation = exitLocation;
                        nearestEnterLocationDistance = exitDistance;
                        faceDirection = EnterDirToFacingDirection(CastExitDirToEnterDir(exit.ExitDir));
                    }
                }
                who.Position = nearestEnterLocation;
                // who.faceDirection(faceDirection); // Keep original face direction
            }

            // Finally fix any errors on the border (this still happens according to some bug reports)
            who.Position = new Vector2(
                Math.Max(0, Math.Min((mapWidth - 1) * 64, who.Position.X)),
                Math.Max(0, Math.Min((mapHeight - 1) * 64, who.Position.Y))
                );
        }

        public void AddPlayer(Farmer who)
        {
            if (who == Game1.player)
            {
                // Fix enter position (some bug I haven't figured out yet spawns network clients outside the map delimiter...)
                FixPlayerPosAfterWarp(who);
                DeepWoodsManager.currentDeepWoods = this;
            }
            if (!Game1.IsMasterGame)
                return;
            this.hasEverBeenVisited.Value = true;
            this.playerCount.Value = this.playerCount.Value + 1;
            ValidateAndIfNecessaryCreateExitChildren();
        }

        public void ValidateAndIfNecessaryCreateExitChildren()
        {
            if (!Game1.IsMasterGame)
                return;

            if (this.playerCount <= 0)
                return;

            if (this.level.Value > 1 && this.Parent == null && !this.HasExit(CastEnterDirToExitDir(this.EnterDir)))
            {
                // this.abandonedByParentTime = Game1.timeOfDay;
                this.exits.Add(new DeepWoodsExit(this, CastEnterDirToExitDir(this.EnterDir), this.EnterLocation));
            }

            foreach (var exit in this.exits)
            {
                if (exit.TargetDeepWoodsName == null)
                    exit.TargetDeepWoodsName = "DeepWoods_" + DeepWoodsRandom.CalculateSeed(level + 1, ExitDirToEnterDir(exit.ExitDir), Seed);
                DeepWoods exitDeepWoods = Game1.getLocationFromName(exit.TargetDeepWoodsName) as DeepWoods;
                if (exitDeepWoods == null)
                {
                    exitDeepWoods = new DeepWoods(this, this.level + 1, ExitDirToEnterDir(exit.ExitDir));
                    DeepWoodsManager.AddDeepWoodsToGameLocations(exitDeepWoods);
                }
                exit.TargetDeepWoodsName = exitDeepWoods.Name;
                exit.TargetLocation = exitDeepWoods.EnterLocation;
            }
        }


        private DeepWoodsExit GetExit(ExitDirection exitDir)
        {
            foreach (var exit in this.exits)
            {
                if (exit.ExitDir == exitDir)
                {
                    return exit;
                }
            }
            return null;
        }

        private bool HasExit(ExitDirection exitDir)
        {
            return GetExit(exitDir) != null;
        }

        public void RandomizeExits()
        {
            if (!Game1.IsMasterGame)
                return;

            if (!this.hasEverBeenVisited)
                return;

            if (this.level > 1 && !this.HasExit(CastEnterDirToExitDir(this.EnterDir)))
            {
                // this.abandonedByParentTime = Game1.timeOfDay;
                this.parentName.Value = null;
                this.parentExitLocation.Value = Point.Zero;
                this.exits.Add(new DeepWoodsExit(this, CastEnterDirToExitDir(this.EnterDir), this.EnterLocation));
            }

            foreach (var exit in this.exits)
            {
                // Randomize exit if child level exists and has been visited
                if (exit.TargetDeepWoodsName != null
                    && Game1.getLocationFromName(exit.TargetDeepWoodsName) is DeepWoods exitDeepWoods
                    && exitDeepWoods.hasEverBeenVisited)
                {
                    exit.TargetDeepWoodsName = null;
                }
            }

            ValidateAndIfNecessaryCreateExitChildren();
        }

        public bool TryRemove()
        {
            if (!Game1.IsMasterGame)
                throw new ApplicationException("Illegal call to DeepWoods.TryRemove() in client.");

            if (this.level == 1)
                return false;

            if (this.playerCount > 0)
                return false;

            if ((this.Parent?.playerCount ?? 0) > 0 && Game1.timeOfDay <= (this.abandonedByParentTime + TIME_BEFORE_DELETION_ALLOWED))
                return false;

            if (Game1.timeOfDay <= (this.spawnTime + TIME_BEFORE_DELETION_ALLOWED))
                return false;

            foreach (var exit in this.exits)
            {
                if (Game1.getLocationFromName(exit.TargetDeepWoodsName) is DeepWoods exitDeepWoods)
                {
                    exitDeepWoods.parentName.Value = null;
                    exitDeepWoods.parentExitLocation.Value = Point.Zero;
                }
            }

            this.parentName.Value = null;
            this.parentExitLocation.Value = Point.Zero;

            this.exits.Clear();
            this.characters.Clear();
            this.terrainFeatures.Clear();
            this.largeTerrainFeatures.Clear();
            this.resourceClumps.Clear();

            DeepWoodsManager.RemoveDeepWoodsFromGameLocations(this);
            return true;
        }


        private Map CreateEmptyMap(string name, int mapWidth, int mapHeight)
        {
            // Create new map
            Map map = new Map(name);

            // Add outdoor tilesheet
            map.AddTileSheet(new TileSheet(DEFAULT_OUTDOOR_TILESHEET_ID, map, "Maps\\" + Game1.currentSeason.ToLower() + "_outdoorsTileSheet", new Size(25, 79), new Size(16, 16)));
            map.AddTileSheet(new TileSheet(LAKE_TILESHEET_ID, map, "Maps\\deepWoodsLakeTilesheet", new Size(8, 5), new Size(16, 16)));
            map.LoadTileSheets(Game1.mapDisplayDevice);

            // Add default layers
            map.AddLayer(new Layer("Back", map, new xTile.Dimensions.Size(mapWidth, mapHeight), new xTile.Dimensions.Size(64, 64)));
            map.AddLayer(new Layer("Buildings", map, new xTile.Dimensions.Size(mapWidth, mapHeight), new xTile.Dimensions.Size(64, 64)));
            map.AddLayer(new Layer("Front", map, new xTile.Dimensions.Size(mapWidth, mapHeight), new xTile.Dimensions.Size(64, 64)));
            map.AddLayer(new Layer("Paths", map, new xTile.Dimensions.Size(mapWidth, mapHeight), new xTile.Dimensions.Size(64, 64)));
            map.AddLayer(new Layer("AlwaysFront", map, new xTile.Dimensions.Size(mapWidth, mapHeight), new xTile.Dimensions.Size(64, 64)));

            return map;
        }

        public override void updateMap()
        {
            // Always create an empty map, to avoid crashes
            // (give it maximum size, so game doesn't mess with warp locations on network)
            if (this.map == null)
                this.map = CreateEmptyMap("DEEPWOODSEMPTY", Settings.Map.MaxMapWidth, Settings.Map.MaxMapHeight);

            // Check if level is properly initialized
            if (this.Seed == 0)
                return;

            // Check that network data has been sent and initialized by server
            if (!this.HasReceivedNetworkData)
                return;

            // Check if map is already created
            if (this.map != null && this.map.Id == this.Name)
                return;

            // Check that mapWidth and mapHeight are set
            if (mapWidth.Value == 0 || mapHeight.Value == 0)
                return;

            // Create map with proper size
            this.map = CreateEmptyMap(this.Name, mapWidth, mapHeight);

            // Build the map!
            DeepWoodsBuilder.Build(this, this.map, DeepWoodsEnterExit.CreateExitDictionary(this.EnterDir, this.EnterLocation, this.exits));
        }

        // This is the default day update method of GameLocation, called only on the server
        public override void DayUpdate(int dayOfMonth)
        {
            base.DayUpdate(dayOfMonth);

            if (this.level < Settings.Level.MinLevelForFruits)
            {
                foreach (TerrainFeature terrainFeature in this.terrainFeatures.Values)
                {
                    if (terrainFeature is FruitTree fruitTree)
                        fruitTree.fruitsOnTree.Value = 0;
                }
            }
        }

        public void CheckWarp()
        {
            if (Game1.player.currentLocation == this && Game1.locationRequest == null)
            {
                if (Game1.player.Position.X + 48 < 0)
                    Warp(ExitDirection.LEFT);
                else if (Game1.player.Position.Y + 48 < 0)
                    Warp(ExitDirection.TOP);
                else if (Game1.player.Position.X + 16 > this.mapWidth.Value * 64)
                    Warp(ExitDirection.RIGHT);
                else if (Game1.player.Position.Y + 16 > this.mapHeight.Value * 64)
                    Warp(ExitDirection.BOTTOM);
            }
        }

        private void Warp(ExitDirection exitDir)
        {
            if (Game1.locationRequest == null)
            {
                bool warped = false;

                string targetDeepWoodsName = null;
                Location? targetLocationWrapper = null;

                if (level.Value == 1 && exitDir == ExitDirection.TOP)
                {
                    targetDeepWoodsName = "Woods";
                    targetLocationWrapper = WOODS_WARP_LOCATION;
                }
                else if (GetExit(exitDir) is DeepWoodsExit exit)
                {
                    targetDeepWoodsName = exit.TargetDeepWoodsName;
                    if (exit.TargetLocation.X == 0 && exit.TargetLocation.Y == 0)
                    {
                        DeepWoods exitDeepWoods = Game1.getLocationFromName(targetDeepWoodsName) as DeepWoods;
                        if (exitDeepWoods != null)
                            exit.TargetLocation = new Location(exitDeepWoods.enterLocation.X, exitDeepWoods.enterLocation.Y);
                    }
                    targetLocationWrapper = exit.TargetLocation;
                }
                else if (CastEnterDirToExitDir(EnterDir) == exitDir)
                {
                    targetDeepWoodsName = parentName.Value;
                    if (ParentExitLocation.X == 0 && ParentExitLocation.Y == 0)
                    {
                        DeepWoods parentDeepWoods = Game1.getLocationFromName(targetDeepWoodsName) as DeepWoods;
                        if (parentDeepWoods != null)
                            ParentExitLocation = parentDeepWoods.GetExit(EnterDirToExitDir(EnterDir)).Location;
                    }
                    targetLocationWrapper = ParentExitLocation;
                }

                ModEntry.Log("Trying to warp from " + this.Name + ": (ExitDir: " + exitDir + ", Position: " + Game1.player.Position.X + ", " + Game1.player.Position.Y + ", targetDeepWoodsName: " + targetDeepWoodsName + ", targetLocation: " + (targetLocationWrapper?.X ?? -1) + ", " + (targetLocationWrapper?.Y ?? -1) + ")", LogLevel.Debug);

                if (targetLocationWrapper.HasValue && targetDeepWoodsName != null)
                {
                    Location targetLocation = targetLocationWrapper.Value;

                    if (!(targetLocation.X == 0 && targetLocation.Y == 0))
                    {
                        if (exitDir == ExitDirection.LEFT)
                            targetLocation.X += 1;
                        else if (exitDir == ExitDirection.BOTTOM)
                            targetLocation.Y += 1;

                        if (targetDeepWoodsName != "Woods")
                        {
                            DeepWoodsManager.currentWarpRequestName = targetDeepWoodsName;
                            DeepWoodsManager.currentWarpRequestLocation = new Vector2(targetLocation.X * 64, targetLocation.Y * 64);
                            if (!Game1.IsMasterGame)
                                DeepWoodsManager.AddBlankDeepWoodsToGameLocations(targetDeepWoodsName);
                        }

                        Game1.warpFarmer(targetDeepWoodsName, targetLocation.X, targetLocation.Y, false);
                        warped = true;
                    }
                }

                if (!warped)
                {
                    ModEntry.Log("Warp from " + this.Name + " failed. (ExitDir: " + exitDir + ")", LogLevel.Warn);
                }
            }
        }

        public override bool isCollidingPosition(Microsoft.Xna.Framework.Rectangle position, xTile.Dimensions.Rectangle viewport, bool isFarmer, int damagesFarmer, bool glider, Character character)
        {
            if (!glider)
            {
                foreach (ResourceClump resourceClump in this.resourceClumps)
                {
                    if (resourceClump.getBoundingBox(resourceClump.tile).Intersects(position))
                        return true;
                }
            }
            return base.isCollidingPosition(position, viewport, isFarmer, damagesFarmer, glider, character);
        }

        public override bool performToolAction(Tool t, int tileX, int tileY)
        {
            foreach (ResourceClump resourceClump in this.resourceClumps)
            {
                if (resourceClump.occupiesTile(tileX, tileY))
                {
                    if (resourceClump.performToolAction(t, 1, resourceClump.tile, this))
                    {
                        this.resourceClumps.Remove(resourceClump);
                    }
                    return true;
                }
            }
            return false;
        }

        public bool IsLocationOnBorderOrExit(Vector2 v)
        {
            // No placements on border tiles.
            if (v.X <= 0 || v.Y <= 0 || v.X >= (mapWidth.Value - 2) || v.Y >= (mapHeight.Value - 2))
                return true;

            // No placements on exits.
            foreach (var exit in this.exits)
            {
                Microsoft.Xna.Framework.Rectangle exitRectangle = new Microsoft.Xna.Framework.Rectangle(exit.Location.X - Settings.Map.ExitRadius, exit.Location.Y - Settings.Map.ExitRadius, Settings.Map.ExitRadius * 2 + 1, Settings.Map.ExitRadius * 2 + 1);
                if (exitRectangle.Contains((int)v.X, (int)v.Y))
                {
                    return true;
                }
            }

            // No placements on enter location as well.
            Microsoft.Xna.Framework.Rectangle enterRectangle = new Microsoft.Xna.Framework.Rectangle(enterLocation.X - Settings.Map.ExitRadius, enterLocation.Y - Settings.Map.ExitRadius, Settings.Map.ExitRadius * 2 + 1, Settings.Map.ExitRadius * 2 + 1);
            if (enterRectangle.Contains((int)v.X, (int)v.Y))
            {
                return true;
            }

            return false;
        }

        public override bool isTileLocationTotallyClearAndPlaceable(Vector2 v)
        {
            // No placements on tiles that are covered in forest.
            if (this.map.GetLayer("Buildings").Tiles[(int)v.X, (int)v.Y] != null)
                return false;

            // No placements on borders, exits and enter locations.
            if (IsLocationOnBorderOrExit(v))
                return false;

            // No placements if something is placed here already.
            foreach (ResourceClump resourceClump in this.resourceClumps)
            {
                if (resourceClump.occupiesTile((int)v.X, (int)v.Y))
                    return false;
            }

            // No placements if something is placed here already.
            foreach (LargeTerrainFeature largeTerrainFeature in this.largeTerrainFeatures)
            {
                if (largeTerrainFeature.getBoundingBox().Intersects(new Microsoft.Xna.Framework.Rectangle((int)v.X * 64, (int)v.Y * 64, 64, 64)))
                    return false;
            }

            // Call parent method for further checks.
            return base.isTileLocationTotallyClearAndPlaceable(v);
        }

        public override bool isTileOccupied(Vector2 tileLocation, string characterToIgnore = "")
        {
            // Check resourceClumps.
            foreach (ResourceClump resourceClump in this.resourceClumps)
            {
                if (resourceClump.occupiesTile((int)tileLocation.X, (int)tileLocation.Y))
                    return true;
            }

            // Call parent method for further checks.
            return base.isTileOccupied(tileLocation, characterToIgnore);
        }

        public bool CanPlaceMonsterHere(int x, int y, Monster monster)
        {
            Microsoft.Xna.Framework.Rectangle rectangle = monster.GetBoundingBox();
            rectangle.X = x;
            rectangle.Y = y;
            rectangle.Width /= 16;
            rectangle.Height /= 16;

            foreach (NPC npc in this.characters)
            {
                if (npc.GetBoundingBox().Intersects(rectangle))
                    return false;
            }

            for (int i = 0; i < rectangle.Width; i++)
            {
                for (int j = 0; j < rectangle.Height; j++)
                {
                    Vector2 v = new Vector2(x + i, y + j);

                    if (IsLocationOnBorderOrExit(v))
                        return false;

                    if (!monster.isGlider
                        && !isTileLocationTotallyClearAndPlaceable(v)
                        && !(this.terrainFeatures.ContainsKey(v) && this.terrainFeatures[v] is Grass))
                        return false;
                }
            }

            return true;
        }

        protected override void resetSharedState()
        {
            base.resetSharedState();
        }

        protected override void resetLocalState()
        {
            base.resetLocalState();

            // TODO: Better critter spawning in forest
            this.tryToAddCritters(false);

            DeepWoodsDebris.Initialize(this);

            foreach (Vector2 lightSource in this.lightSources)
            {
                Game1.currentLightSources.Add(new LightSource(LightSource.indoorWindowLight, lightSource * 64f, 1.0f));
            }

            DeepWoodsManager.FixLighting();
        }

        public override void performTenMinuteUpdate(int timeOfDay)
        {
            base.performTenMinuteUpdate(timeOfDay);

            // TODO: Better critter spawning in forest
            if (this.map != null)
                this.tryToAddCritters(true);
        }

        public override void checkForMusic(GameTime time)
        {
            if (Game1.currentSong != null && Game1.currentSong.IsPlaying || Game1.nextMusicTrack != null && Game1.nextMusicTrack.Length != 0)
                return;

            if (Game1.isRaining)
            {
                Game1.changeMusicTrack("rain");
            }
            else
            {
                if (Game1.timeOfDay < 2500)
                {
                    if (Game1.random.NextDouble() < 0.75)
                    {
                        Game1.changeMusicTrack("woodsTheme");
                    }
                    else
                    {
                        if (Game1.isDarkOut())
                        {
                            if (Game1.currentSeason != "winter")
                            {
                                Game1.changeMusicTrack("spring_night_ambient");
                            }
                        }
                        else
                        {
                            Game1.changeMusicTrack(Game1.currentSeason + "_day_ambient");
                        }
                    }
                }
            }
        }

        public override void cleanupBeforePlayerExit()
        {
            base.cleanupBeforePlayerExit();
            DeepWoodsDebris.Clear(this);
            Game1.changeMusicTrack("");
        }

        public override void updateEvenIfFarmerIsntHere(GameTime time, bool skipWasUpdatedFlush = false)
        {
            base.updateEvenIfFarmerIsntHere(time, skipWasUpdatedFlush);
        }

        public override void UpdateWhenCurrentLocation(GameTime time)
        {
            base.UpdateWhenCurrentLocation(time);
            DeepWoodsDebris.Update(this, time);
            foreach (ResourceClump resourceClump in this.resourceClumps)
            {
                resourceClump.tickUpdate(time, resourceClump.tile, this);
            }
        }

        public override void draw(SpriteBatch b)
        {
            base.draw(b);
            foreach (ResourceClump resourceClump in this.resourceClumps)
            {
                resourceClump.draw(b, resourceClump.tile);
            }
        }

        public override void drawAboveAlwaysFrontLayer(SpriteBatch b)
        {
            base.drawAboveAlwaysFrontLayer(b);
            foreach (var character in this.characters)
            {
                (character as Monster)?.drawAboveAllLayers(b);
            }
            DeepWoodsDebris.Draw(this, b);
        }

        public void DrawLevelDisplay()
        {
            // Game1.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, (DepthStencilState)null, (RasterizerState)null);

            string currentLevelAsString = string.Concat(this.level);
            Location titleSafeTopLeftCorner = new DeepWoodsSpaceManager(this.mapWidth.Value, this.mapHeight.Value).GetActualTitleSafeTopleftCorner();

            SpriteText.drawString(
                Game1.spriteBatch,
                currentLevelAsString,
                titleSafeTopLeftCorner.X + 16, titleSafeTopLeftCorner.Y + 16, /*x,y*/
                999999, -1, 999999, /*charPos,width,height*/
                1f, 1f, /*alpha,depth*/
                false, /*junimoText*/
                SpriteText.scrollStyle_darkMetal,
                "", /*placeHolderScrollWidthText*/
                SpriteText.color_Green);

            // Game1.spriteBatch.End();
        }

        public override StardewValley.Object getFish(float millisecondsAfterNibble, int bait, int waterDepth, Farmer who, double baitPotency)
        {
            return this.getFish(millisecondsAfterNibble, bait, waterDepth, who, baitPotency, (string)null);
        }

        public override StardewValley.Object getFish(float millisecondsAfterNibble, int bait, int waterDepth, Farmer who, double baitPotency, string locationName = null)
        {
            if ((locationName != null && locationName != this.Name) || !CanHazAwesomeFish())
            {
                return base.getFish(millisecondsAfterNibble, bait, waterDepth, who, baitPotency, locationName);
            }
            return new StardewValley.Object(GetRandomAwesomeFish(), 1, false, -1, 0);
        }

        private bool CanHazAwesomeFish()
        {
            return new DeepWoodsRandom(this, this.Seed ^ Game1.currentGameTime.TotalGameTime.Milliseconds ^ Game1.random.Next()).CheckChance(Settings.Luck.Fishies.ChanceForAwesomeFish);
        }

        private int GetRandomAwesomeFish()
        {
            return new DeepWoodsRandom(this, this.Seed ^ Game1.currentGameTime.TotalGameTime.Milliseconds ^ Game1.random.Next()).GetRandomValue(Settings.Luck.Fishies.AwesomeFishies);
        }

        public int GetCombatLevel()
        {
            int parentCombatLevel = this.Parent?.GetCombatLevel() ?? 0;
            int totalCombatLevel = 0;
            int totalCombatLevelCount = 0;
            foreach (Farmer farmer in this.farmers)
            {
                totalCombatLevel += farmer.CombatLevel;
                totalCombatLevelCount++;
            }
            if (totalCombatLevelCount > 0)
            {
                return parentCombatLevel + totalCombatLevel / totalCombatLevelCount;
            }
            else
            {
                return parentCombatLevel;
            }
        }

        public int GetLuckLevel()
        {
            int totalLuckLevel = 0;
            int totalLuckLevelCount = 0;
            foreach (Farmer farmer in Game1.getOnlineFarmers())
            {
                if (farmer.currentLocation == this || farmer.currentLocation == this.Parent)
                {
                    totalLuckLevel += farmer.LuckLevel;
                    totalLuckLevelCount++;
                }
            }
            if (totalLuckLevelCount > 0)
            {
                return totalLuckLevel / totalLuckLevelCount;
            }
            else
            {
                return 0;
            }
        }
    }
}
