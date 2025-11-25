using ACE.Common;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Factories.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using System;
using System.Linq;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        public void HandleActionAbandonContract(uint contractId)
        {
            ContractManager.Abandon(contractId);
        }

        public void RefreshExplorationAssignments(WorldObject sourceObject = null, bool confirmed = false, bool fromAcademy = false)
        {
            if (Common.ConfigManager.Config.Server.WorldRuleset != Common.Ruleset.CustomDM)
                return;

            if (!fromAcademy && !confirmed && Exploration1KillProgressTracker != 0)
            {
                if (sourceObject != null)
                {
                    if (!ConfirmationManager.EnqueueSend(new Confirmation_Custom(Guid, () => RefreshExplorationAssignments(sourceObject, true), () => GiveFromEmote(sourceObject, (uint)Factories.Enum.WeenieClassName.blankExplorationContract)), "This will overwrite all current exploration assignments.\nProceed?"))
                        SendWeenieError(WeenieError.ConfirmationInProgress);
                }
                else
                {
                    if (!ConfirmationManager.EnqueueSend(new Confirmation_Custom(Guid, () => RefreshExplorationAssignments(sourceObject, true)), "This will overwrite all current exploration assignments.\nProceed?"))
                        SendWeenieError(WeenieError.ConfirmationInProgress);
                }
                return;
            }

            bool useName = sourceObject?.Name.Length > 0;
            var msg = $"{(useName ? $"{sourceObject.Name} tells you, \"" : "")}Unfortunately there's no available exploration assignments suited for you at the moment.{(useName ? $"\"" : "")}";

            if (useName)
            {
                // Cleanup extra contracts that might have cropped up.
                TryConsumeFromEquippedObjectsWithNetworking((uint)Factories.Enum.WeenieClassName.explorationContract);
                TryConsumeFromInventoryWithNetworking((uint)Factories.Enum.WeenieClassName.explorationContract);
            }

            var level = Level ?? 1;
            var minLevel = Math.Max(level - (int)Math.Ceiling(level * 0.1f), 1);
            var maxLevel = level + (int)Math.Ceiling(level * 0.2f);
            if (level > 100)
                maxLevel = int.MaxValue;
            var explorationList = DatabaseManager.World.GetExplorationSitesByLevelRange(minLevel, maxLevel, level);

            if (explorationList.Count == 0)
            {
                if (!fromAcademy)
                    Session.Network.EnqueueSend(new GameMessageSystemChat(msg, useName ? ChatMessageType.Tell : ChatMessageType.Broadcast));

                if (sourceObject != null)
                    GiveFromEmote(sourceObject, (uint)Factories.Enum.WeenieClassName.blankExplorationContract); // Return Blank Contract.

                Exploration1LandblockId = 0;
                Exploration1KillProgressTracker = 0;
                Exploration1MarkerProgressTracker = 0;
                Exploration1Description = "";
                Exploration1LandblockReached = false;

                Exploration2LandblockId = 0;
                Exploration2KillProgressTracker = 0;
                Exploration2MarkerProgressTracker = 0;
                Exploration2Description = "";
                Exploration2LandblockReached = false;

                Exploration3LandblockId = 0;
                Exploration3KillProgressTracker = 0;
                Exploration3MarkerProgressTracker = 0;
                Exploration3Description = "";
                Exploration3LandblockReached = false;
                return;
            }

            // Avoid giving the same contracts the player already has active.
            var nonRepeatList = explorationList.Where(c => c.Landblock != Exploration1LandblockId && c.Landblock != Exploration2LandblockId && c.Landblock != Exploration3LandblockId).ToList();
            if (nonRepeatList.Count >= 3) // Only remove repeated contracts if there are enough different ones to fill a new contract.
                explorationList = nonRepeatList;

            var roll = ThreadSafeRandom.Next(0, explorationList.Count - 1);
            var entry = explorationList[roll];
            explorationList.RemoveAt(roll);

            var modifiedCreatureCount = Math.Clamp(entry.CreatureCount, 10, 200);
            var explorationKillAmount = (int)ThreadSafeRandom.Next(modifiedCreatureCount, modifiedCreatureCount * 2.0f);
            var explorationMarkerAmount = Math.Clamp(1 + (int)Math.Floor(explorationKillAmount / 30f), 1, 10);

            Exploration1LandblockId = entry.Landblock;
            Exploration1KillProgressTracker = explorationKillAmount;
            Exploration1MarkerProgressTracker = explorationMarkerAmount;
            Exploration1LandblockReached = false;

            string entryName;
            string entryDirections;
            var entryLandblock = DatabaseManager.World.GetLandblockDescriptionsByLandblock((ushort)entry.Landblock).FirstOrDefault();
            if (entryLandblock != null)
            {
                entryName = entryLandblock.Name;
                if (entryLandblock.MicroRegion != "")
                    entryDirections = $"{entryLandblock.Directions} {entryLandblock.Reference} in {entryLandblock.MicroRegion}";
                else if (entryLandblock.MacroRegion != "" && entryLandblock.MacroRegion != "Dereth")
                    entryDirections = $"{entryLandblock.Directions} {entryLandblock.Reference} in {entryLandblock.MacroRegion}";
                else
                    entryDirections = $"{entryLandblock.Directions} {entryLandblock.Reference}";
            }
            else
            {
                entryName = $"unknown location({entry.Landblock})";
                entryDirections = "at an unknown location";
            }

            Exploration1Description = $"Explore {entryName} by killing {explorationKillAmount:N0} {entry.ContentDescription} and finding {explorationMarkerAmount:N0} exploration marker{(explorationMarkerAmount > 1 ? "s" : "")}. It is located {entryDirections}.";
            msg = $"{(useName ? $"{sourceObject.Name} tells you, \"" : "")}{Exploration1Description}{(useName ? $"\"" : "")}";

            if (useName && !fromAcademy)
                Session.Network.EnqueueSend(new GameMessageSystemChat($"{sourceObject.Name} tells you, \"Here's your assignments:\"", ChatMessageType.Tell));

            Session.Network.EnqueueSend(new GameMessageSystemChat(msg, useName ? ChatMessageType.Tell : ChatMessageType.Broadcast));

            if (explorationList.Count != 0 && !fromAcademy)
            {
                roll = ThreadSafeRandom.Next(0, explorationList.Count - 1);
                entry = explorationList[roll];
                explorationList.RemoveAt(roll);
                modifiedCreatureCount = Math.Clamp(entry.CreatureCount, 10, 200);
                explorationKillAmount = (int)ThreadSafeRandom.Next(modifiedCreatureCount, modifiedCreatureCount * 2.0f);
                explorationMarkerAmount = Math.Clamp(1 + (int)Math.Floor(explorationKillAmount / 30f), 1, 10);

                Exploration2LandblockId = entry.Landblock;
                Exploration2KillProgressTracker = explorationKillAmount;
                Exploration2MarkerProgressTracker = explorationMarkerAmount;
                Exploration2LandblockReached = false;

                entryLandblock = DatabaseManager.World.GetLandblockDescriptionsByLandblock((ushort)entry.Landblock).FirstOrDefault();
                if (entryLandblock != null)
                {
                    entryName = entryLandblock.Name;
                    if (entryLandblock.MicroRegion != "")
                        entryDirections = $"{entryLandblock.Directions} {entryLandblock.Reference} in {entryLandblock.MicroRegion}";
                    else if (entryLandblock.MacroRegion != "" && entryLandblock.MacroRegion != "Dereth")
                        entryDirections = $"{entryLandblock.Directions} {entryLandblock.Reference} in {entryLandblock.MacroRegion}";
                    else
                        entryDirections = $"{entryLandblock.Directions} {entryLandblock.Reference}";
                }
                else
                {
                    entryName = $"unknown location({entry.Landblock})";
                    entryDirections = "at an unknown location";
                }

                Exploration2Description = $"Explore {entryName} by killing {explorationKillAmount:N0} {entry.ContentDescription} and finding {explorationMarkerAmount:N0} exploration marker{(explorationMarkerAmount > 1 ? "s" : "")}. It is located {entryDirections}.";
                msg = $"{(useName ? $"{sourceObject.Name} tells you, \"" : "")}{Exploration2Description}{(useName ? $"\"" : "")}";

                Session.Network.EnqueueSend(new GameMessageSystemChat(msg, useName ? ChatMessageType.Tell : ChatMessageType.Broadcast));
            }
            else
            {
                Exploration2LandblockId = 0;
                Exploration2KillProgressTracker = 0;
                Exploration2MarkerProgressTracker = 0;
                Exploration2Description = "";
                Exploration2LandblockReached = false;
            }

            if (explorationList.Count != 0 && !fromAcademy)
            {
                roll = ThreadSafeRandom.Next(0, explorationList.Count - 1);
                entry = explorationList[roll];
                explorationList.RemoveAt(roll);
                modifiedCreatureCount = Math.Clamp(entry.CreatureCount, 10, 200);
                explorationKillAmount = (int)ThreadSafeRandom.Next(modifiedCreatureCount, modifiedCreatureCount * 2.0f);
                explorationMarkerAmount = Math.Clamp(1 + (int)Math.Floor(explorationKillAmount / 30f), 1, 10);

                Exploration3LandblockId = entry.Landblock;
                Exploration3KillProgressTracker = explorationKillAmount;
                Exploration3MarkerProgressTracker = explorationMarkerAmount;
                Exploration3LandblockReached = false;

                entryLandblock = DatabaseManager.World.GetLandblockDescriptionsByLandblock((ushort)entry.Landblock).FirstOrDefault();
                if (entryLandblock != null)
                {
                    entryName = entryLandblock.Name;
                    if (entryLandblock.MicroRegion != "")
                        entryDirections = $"{entryLandblock.Directions} {entryLandblock.Reference} in {entryLandblock.MicroRegion}";
                    else if (entryLandblock.MacroRegion != "" && entryLandblock.MacroRegion != "Dereth")
                        entryDirections = $"{entryLandblock.Directions} {entryLandblock.Reference} in {entryLandblock.MacroRegion}";
                    else
                        entryDirections = $"{entryLandblock.Directions} {entryLandblock.Reference}";
                }
                else
                {
                    entryName = $"unknown location({entry.Landblock})";
                    entryDirections = "at an unknown location";
                }

                Exploration3Description = $"Explore {entryName} by killing {explorationKillAmount:N0} {entry.ContentDescription} and finding {explorationMarkerAmount:N0} exploration marker{(explorationMarkerAmount > 1 ? "s" : "")}. It is located {entryDirections}.";
                msg = $"{(useName ? $"{sourceObject.Name} tells you, \"" : "")}{Exploration3Description}{(useName ? $"\"" : "")}";

                Session.Network.EnqueueSend(new GameMessageSystemChat(msg, useName ? ChatMessageType.Tell : ChatMessageType.Broadcast));
            }
            else
            {
                Exploration3LandblockId = 0;
                Exploration3KillProgressTracker = 0;
                Exploration3MarkerProgressTracker = 0;
                Exploration3Description = "";
                Exploration3LandblockReached = false;
            }

            if (sourceObject != null)
            {
                if (!fromAcademy)
                    QuestManager.Stamp("ExplorationAssignmentsGiven");
                GiveFromEmote(sourceObject, (uint)Factories.Enum.WeenieClassName.explorationContract); // Give new contract.
            }
        }

        public void RewardExplorationAssignments(WorldObject sourceObject = null, bool confirmed = false)
        {
            Session.Network.EnqueueSend(new GameMessageSystemChat($"Debug: RewardExplorationAssignments called - Player Level: {Level}", ChatMessageType.System));
            bool useName = sourceObject?.Name.Length > 0;

            var hasAssignment1 = false;
            var hasAssignment2 = false;
            var hasAssignment3 = false;
            var assignment1Complete = false;
            var assignment2Complete = false;
            var assignment3Complete = false;
            if (Exploration1LandblockId != 0 && Exploration1Description.Length > 0)
            {
                hasAssignment1 = true;
                assignment1Complete = Exploration1LandblockReached && Exploration1KillProgressTracker <= 0 && Exploration1MarkerProgressTracker <= 0;
                Session.Network.EnqueueSend(new GameMessageSystemChat($"Debug: Assignment 1 - Reached:{Exploration1LandblockReached} Kills:{Exploration1KillProgressTracker} Markers:{Exploration1MarkerProgressTracker} Complete:{assignment1Complete}", ChatMessageType.System));
            }
            if (Exploration2LandblockId != 0 && Exploration2Description.Length > 0)
            {
                hasAssignment2 = true;
                assignment2Complete = Exploration2LandblockReached && Exploration2KillProgressTracker <= 0 && Exploration2MarkerProgressTracker <= 0;
                Session.Network.EnqueueSend(new GameMessageSystemChat($"Debug: Assignment 2 - Reached:{Exploration2LandblockReached} Kills:{Exploration2KillProgressTracker} Markers:{Exploration2MarkerProgressTracker} Complete:{assignment2Complete}", ChatMessageType.System));
            }
            if (Exploration3LandblockId != 0 && Exploration3Description.Length > 0)
            {
                hasAssignment3 = true;
                assignment3Complete = Exploration3LandblockReached && Exploration3KillProgressTracker <= 0 && Exploration3MarkerProgressTracker <= 0;
                Session.Network.EnqueueSend(new GameMessageSystemChat($"Debug: Assignment 3 - Reached:{Exploration3LandblockReached} Kills:{Exploration3KillProgressTracker} Markers:{Exploration3MarkerProgressTracker} Complete:{assignment3Complete}", ChatMessageType.System));
            }

            if (useName)
            {
                // Cleanup extra contracts that might have cropped up.
                TryConsumeFromEquippedObjectsWithNetworking((uint)Factories.Enum.WeenieClassName.explorationContract);
                TryConsumeFromInventoryWithNetworking((uint)Factories.Enum.WeenieClassName.explorationContract);
            }

            if (!hasAssignment1 && !hasAssignment2 && !hasAssignment3)
                Session.Network.EnqueueSend(new GameMessageSystemChat($"{(useName ? $"{sourceObject.Name} tells you, \"" : "")}{"You have no assignments!"}{(useName ? $"\"" : "")}", useName ? ChatMessageType.Tell : ChatMessageType.Broadcast));
            else if (!assignment1Complete && !assignment2Complete && !assignment3Complete)
                Session.Network.EnqueueSend(new GameMessageSystemChat($"{(useName ? $"{sourceObject.Name} tells you, \"" : "")}{"None of your assignments are complete!"}{(useName ? $"\"" : "")}", useName ? ChatMessageType.Tell : ChatMessageType.Broadcast));
            else
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat($"{sourceObject.Name} tells you, \"Here's your reward for the completed assignments:\"", ChatMessageType.Tell));

                // Grant XP for completed contracts using reward-by-level system (once a day frequency)
                int completedCount = (assignment1Complete ? 1 : 0) + (assignment2Complete ? 1 : 0) + (assignment3Complete ? 1 : 0);

                // Declare playerLevel once to avoid duplicate declaration error
                int playerLevel = Level ?? 1;

                // Calculate and grant actual XP using tiered exponential system
                if (completedCount > 0)
                {
                    long xpPerAssignment;

                    // Tiered exponential XP calculation (Level² × multiplier)
                    if (playerLevel <= 20)
                    {
                        // Tier 1: Level² × 50 (50-20,000 XP range)
                        xpPerAssignment = (long)(playerLevel * playerLevel * 50);
                    }
                    else if (playerLevel <= 55)
                    {
                        // Tier 2: Level² × 75 (33,075-226,875 XP range)
                        xpPerAssignment = (long)(playerLevel * playerLevel * 75);
                    }
                    else
                    {
                        // Tier 3: Level² × 100 (313,600+ XP)
                        xpPerAssignment = (long)(playerLevel * playerLevel * 100);
                    }

                    long totalXP = xpPerAssignment * completedCount;

                    // Create the XP message
                    var xpMsg = $"You've earned {totalXP:N0} experience for completing {completedCount} exploration assignment{(completedCount != 1 ? "s" : "")}!";

                    // Grant the XP with the message parameter
                    EarnXP(totalXP, XpType.Quest, null, null, 0, null, ShareType.None, xpMsg);
                }

                // Calculate reward tier directly based on level ranges
                // Simplified to match the 3 actual reward tiers in the code:
                // Tier 1-2: Low tier rewards (levels 1-20)
                // Tier 3-5: Mid tier rewards (levels 21-55)  
                // Tier 6-7: High tier rewards (levels 56+)
                int rewardTier;

                if (playerLevel <= 20)
                {
                    // Low tier rewards - use tier 1 (will trigger the <= 2 condition)
                    rewardTier = 1;
                    Session.Network.EnqueueSend(new GameMessageSystemChat($"Debug: Level {playerLevel} = Tier {rewardTier} (Low tier rewards)", ChatMessageType.System));
                }
                else if (playerLevel <= 55)
                {
                    // Mid tier rewards - use tier 3 (will trigger the <= 5 condition)
                    rewardTier = 3;
                    Session.Network.EnqueueSend(new GameMessageSystemChat($"Debug: Level {playerLevel} = Tier {rewardTier} (Mid tier rewards)", ChatMessageType.System));
                }
                else
                {
                    // High tier rewards - use tier 6 (will trigger the else condition)
                    rewardTier = 6;
                    Session.Network.EnqueueSend(new GameMessageSystemChat($"Debug: Level {playerLevel} = Tier {rewardTier} (High tier rewards)", ChatMessageType.System));
                }

                int rewardAmount;

                // ASSIGNMENT 1 - COMPLETE BLOCK WITH TIERED REWARDS
                if (assignment1Complete)
                {
                    rewardAmount = ThreadSafeRandom.Next(3, 5);
                    for (int i = 0; i < rewardAmount; i++)
                        TryGiveRandomSalvage(sourceObject, rewardTier);

                    // Give 3 tiered WCID items as bonus rewards
                    if (sourceObject != null)
                    {
                        if (rewardTier <= 2)
                        {
                            // Low tier rewards (levels 1-20)
                            GiveFromEmote(sourceObject, 116057);  // REPLACE WITH YOUR LOW TIER WCID #1
                            GiveFromEmote(sourceObject, 2225084);  // REPLACE WITH YOUR LOW TIER WCID #2
                            GiveFromEmote(sourceObject, 4616);  // REPLACE WITH YOUR LOW TIER WCID #3
                        }
                        else if (rewardTier <= 5)
                        {
                            // Mid tier rewards (levels 20-50) - YOUR CURRENT ITEMS
                            GiveFromEmote(sourceObject, 36518);
                            GiveFromEmote(sourceObject, 1115084);
                            GiveFromEmote(sourceObject, 44240111);
                        }
                        else
                        {
                            // High tier rewards (levels 55+)
                            GiveFromEmote(sourceObject, 44240111);  // REPLACE WITH YOUR HIGH TIER WCID #1
                            GiveFromEmote(sourceObject, 20630);  // REPLACE WITH YOUR HIGH TIER WCID #2
                            GiveFromEmote(sourceObject, 50184);  // REPLACE WITH YOUR HIGH TIER WCID #3
                        }
                    }

                    Exploration1LandblockId = 0;
                    Exploration1KillProgressTracker = 0;
                    Exploration1MarkerProgressTracker = 0;
                    Exploration1Description = "";
                    Exploration1LandblockReached = false;
                }

                // ASSIGNMENT 2 - COMPLETE BLOCK WITH TIERED REWARDS
                if (assignment2Complete)
                {
                    rewardAmount = ThreadSafeRandom.Next(3, 5);
                    for (int i = 0; i < rewardAmount; i++)
                        TryGiveRandomSalvage(sourceObject, rewardTier);

                    // Give 3 tiered WCID items as bonus rewards
                    if (sourceObject != null)
                    {
                        if (rewardTier <= 2)
                        {
                            // Low tier rewards (levels 1-20)
                            GiveFromEmote(sourceObject, 116057);  // REPLACE WITH YOUR LOW TIER WCID #1
                            GiveFromEmote(sourceObject, 4616);  // REPLACE WITH YOUR LOW TIER WCID #2
                            GiveFromEmote(sourceObject, 2225084);  // REPLACE WITH YOUR LOW TIER WCID #3
                        }
                        else if (rewardTier <= 5)
                        {
                            // Mid tier rewards (levels 20-50) - YOUR CURRENT ITEMS
                            GiveFromEmote(sourceObject, 36518);
                            GiveFromEmote(sourceObject, 1115084);
                            GiveFromEmote(sourceObject, 44240111);
                        }
                        else
                        {
                            // High tier rewards (levels 55+)
                            GiveFromEmote(sourceObject, 44240111);  // REPLACE WITH YOUR HIGH TIER WCID #1
                            GiveFromEmote(sourceObject, 20630);  // REPLACE WITH YOUR HIGH TIER WCID #2
                            GiveFromEmote(sourceObject, 50184);  // REPLACE WITH YOUR HIGH TIER WCID #3
                        }
                    }

                    Exploration2LandblockId = 0;
                    Exploration2KillProgressTracker = 0;
                    Exploration2MarkerProgressTracker = 0;
                    Exploration2Description = "";
                    Exploration2LandblockReached = false;
                }

                // ASSIGNMENT 3 - COMPLETE BLOCK WITH TIERED REWARDS
                if (assignment3Complete)
                {
                    rewardAmount = ThreadSafeRandom.Next(3, 5);
                    for (int i = 0; i < rewardAmount; i++)
                        TryGiveRandomSalvage(sourceObject, rewardTier);

                    // Give 3 tiered WCID items as bonus rewards
                    if (sourceObject != null)
                    {
                        if (rewardTier <= 2)
                        {
                            // Low tier rewards (levels 1-20)
                            GiveFromEmote(sourceObject, 116057);  // REPLACE WITH YOUR LOW TIER WCID #1
                            GiveFromEmote(sourceObject, 4616);  // REPLACE WITH YOUR LOW TIER WCID #2
                            GiveFromEmote(sourceObject, 2225084);  // REPLACE WITH YOUR LOW TIER WCID #3
                        }
                        else if (rewardTier <= 5)
                        {
                            // Mid tier rewards (levels 20-50) - YOUR CURRENT ITEMS
                            GiveFromEmote(sourceObject, 36518);
                            GiveFromEmote(sourceObject, 1115084);
                            GiveFromEmote(sourceObject, 44240111);
                        }
                        else
                        {
                            // High tier rewards (levels 55+)
                            GiveFromEmote(sourceObject, 44240111);  // REPLACE WITH YOUR HIGH TIER WCID #1
                            GiveFromEmote(sourceObject, 20630);  // REPLACE WITH YOUR HIGH TIER WCID #2
                            GiveFromEmote(sourceObject, 50184);  // REPLACE WITH YOUR HIGH TIER WCID #3
                        }
                    }

                    Exploration3LandblockId = 0;
                    Exploration3KillProgressTracker = 0;
                    Exploration3MarkerProgressTracker = 0;
                    Exploration3Description = "";
                    Exploration3LandblockReached = false;
                }
            }

            if (sourceObject != null && ((hasAssignment1 && !assignment1Complete) || (hasAssignment2 && !assignment2Complete) || (hasAssignment3 && !assignment3Complete)))
            {
                GiveFromEmote(sourceObject, (uint)Factories.Enum.WeenieClassName.explorationContract); // Return contract if there's still unfinished contracts.
            }
        }

        private string GetCurrentLandblockName()
        {
            var landblockDescription = DatabaseManager.World.GetLandblockDescriptionsByLandblock(CurrentLandblock.Id.Landblock).FirstOrDefault();
            if (landblockDescription != null)
                return landblockDescription.Name;
            else
                return null;
        }

        public void CheckExplorationLandblock()
        {
            var currentLandblockId = CurrentLandblock.Id.Raw >> 16;
            if (!Exploration1LandblockReached && Exploration1LandblockId != 0 && Exploration1LandblockId == currentLandblockId)
            {
                Exploration1LandblockReached = true;
                var msg = $"You've reached {GetCurrentLandblockName() ?? "your exploration contract's location"}! {Exploration1KillProgressTracker:N0} kill{(Exploration1KillProgressTracker != 1 ? "s" : "")} remaining and {Exploration1MarkerProgressTracker:N0} marker{(Exploration1MarkerProgressTracker != 1 ? "s" : "")} remaining.";
                EarnXP((int)(((-Level ?? -1) - 1000) * (PropertyManager.GetDouble("exploration_bonus_xp").Item + 0.5)), XpType.Exploration, null, null, 0, null, ShareType.None, msg);
                PlayParticleEffect(PlayScript.AugmentationUseAttribute, Guid);
            }

            if (!Exploration2LandblockReached && Exploration2LandblockId != 0 && Exploration2LandblockId == currentLandblockId)
            {
                Exploration2LandblockReached = true;
                var msg = $"You've reached {GetCurrentLandblockName() ?? "your exploration contract's location"}! {Exploration2KillProgressTracker:N0} kill{(Exploration2KillProgressTracker != 1 ? "s" : "")} remaining and {Exploration2MarkerProgressTracker:N0} marker{(Exploration2MarkerProgressTracker != 1 ? "s" : "")} remaining.";
                EarnXP((int)(((-Level ?? -1) - 1000) * (PropertyManager.GetDouble("exploration_bonus_xp").Item + 0.5)), XpType.Exploration, null, null, 0, null, ShareType.None, msg);
                PlayParticleEffect(PlayScript.AugmentationUseAttribute, Guid);
            }

            if (!Exploration3LandblockReached && Exploration3LandblockId != 0 && Exploration3LandblockId == currentLandblockId)
            {
                Exploration3LandblockReached = true;
                var msg = $"You've reached {GetCurrentLandblockName() ?? "your exploration contract's location"}! {Exploration3KillProgressTracker:N0} kill{(Exploration3KillProgressTracker != 1 ? "s" : "")} remaining and {Exploration3MarkerProgressTracker:N0} marker{(Exploration3MarkerProgressTracker != 1 ? "s" : "")} remaining.";
                EarnXP((int)(((-Level ?? -1) - 1000) * (PropertyManager.GetDouble("exploration_bonus_xp").Item + 0.5)), XpType.Exploration, null, null, 0, null, ShareType.None, msg);
                PlayParticleEffect(PlayScript.AugmentationUseAttribute, Guid);
            }
        }

        public void CheckExplorationLandblock(Landblock landblock)
        {
            if (landblock == null || Common.ConfigManager.Config.Server.WorldRuleset != Common.Ruleset.CustomDM)
                return;

            var landblockId = landblock.Id.Raw >> 16;
            if (!Exploration1LandblockReached && Exploration1LandblockId != 0 && Exploration1LandblockId == landblockId)
            {
                Exploration1LandblockReached = true;
                var msg = $"You've reached {GetCurrentLandblockName() ?? "your exploration contract's location"}! {Exploration1KillProgressTracker:N0} kill{(Exploration1KillProgressTracker != 1 ? "s" : "")} remaining and {Exploration1MarkerProgressTracker:N0} marker{(Exploration1MarkerProgressTracker != 1 ? "s" : "")} remaining.";

                var explorationSite = DatabaseManager.World.GetExplorationSitesByLandblock((ushort)landblockId).FirstOrDefault();
                var level = explorationSite != null ? Math.Min(explorationSite.Level, Level ?? 1) : Level;
                EarnXP((int)(((-Level ?? -1) - 1000) * (PropertyManager.GetDouble("exploration_bonus_xp").Item + 0.5)), XpType.Exploration, null, null, 0, null, ShareType.Fellowship, msg);
                PlayParticleEffect(PlayScript.AugmentationUseAttribute, Guid);
                if (Exploration1KillProgressTracker == 0 && Exploration1MarkerProgressTracker == 0)
                    Session.Network.EnqueueSend(new GameMessageSystemChat("Your exploration assignment is now fulfilled!", ChatMessageType.Broadcast));
            }

            if (!Exploration2LandblockReached && Exploration2LandblockId != 0 && Exploration2LandblockId == landblockId)
            {
                Exploration2LandblockReached = true;
                var msg = $"You've reached {GetCurrentLandblockName() ?? "your exploration contract's location"}! {Exploration2KillProgressTracker:N0} kill{(Exploration2KillProgressTracker != 1 ? "s" : "")} remaining and {Exploration2MarkerProgressTracker:N0} marker{(Exploration2MarkerProgressTracker != 1 ? "s" : "")} remaining.";

                var explorationSite = DatabaseManager.World.GetExplorationSitesByLandblock((ushort)landblockId).FirstOrDefault();
                var level = explorationSite != null ? Math.Min(explorationSite.Level, Level ?? 1) : Level;
                EarnXP((int)(((-Level ?? -1) - 1000) * (PropertyManager.GetDouble("exploration_bonus_xp").Item + 0.5)), XpType.Exploration, null, null, 0, null, ShareType.Fellowship, msg);
                PlayParticleEffect(PlayScript.AugmentationUseAttribute, Guid);
                if (Exploration2KillProgressTracker == 0 && Exploration2MarkerProgressTracker == 0)
                    Session.Network.EnqueueSend(new GameMessageSystemChat("Your exploration assignment is now fulfilled!", ChatMessageType.Broadcast));
            }

            if (!Exploration3LandblockReached && Exploration3LandblockId != 0 && Exploration3LandblockId == landblockId)
            {
                Exploration3LandblockReached = true;
                var msg = $"You've reached {GetCurrentLandblockName() ?? "your exploration contract's location"}! {Exploration3KillProgressTracker:N0} kill{(Exploration3KillProgressTracker != 1 ? "s" : "")} remaining and {Exploration3MarkerProgressTracker:N0} marker{(Exploration3MarkerProgressTracker != 1 ? "s" : "")} remaining.";

                var explorationSite = DatabaseManager.World.GetExplorationSitesByLandblock((ushort)landblockId).FirstOrDefault();
                var level = explorationSite != null ? Math.Min(explorationSite.Level, Level ?? 1) : Level;
                EarnXP((int)(((-Level ?? -1) - 1000) * (PropertyManager.GetDouble("exploration_bonus_xp").Item + 0.5)), XpType.Exploration, null, null, 0, null, ShareType.Fellowship, msg);
                PlayParticleEffect(PlayScript.AugmentationUseAttribute, Guid);
                if (Exploration3KillProgressTracker == 0 && Exploration3MarkerProgressTracker == 0)
                    Session.Network.EnqueueSend(new GameMessageSystemChat("Your exploration assignment is now fulfilled!", ChatMessageType.Broadcast));
            }
        }

        public bool TryGiveRandomSalvage(WorldObject giver = null, int tier = 1, float qualityMod = 0.0f)
        {
            var salvage = LootGenerationFactory.CreateRandomLootObjects_New(tier, qualityMod, TreasureItemCategory.MundaneItem, TreasureItemType_Orig.Salvage);
            var success = false;
            if (salvage != null)
            {
                if (giver != null)
                    success = TryCreateForGive(giver, salvage);
                else
                    success = TryCreateInInventoryWithNetworking(salvage, out _, true);
            }

            if (!success)
                salvage.Destroy();

            return success;
        }
    }
}
