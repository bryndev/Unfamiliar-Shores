using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using log4net;

using ACE.Common;
using ACE.Common.Extensions;
using ACE.Database;
using ACE.Database.Models.World;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Entity.Mutations;
using ACE.Server.Factories;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using Weenie = ACE.Entity.Models.Weenie;

namespace ACE.Server.Managers
{
    public partial class RecipeManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public static Recipe GetRecipe(Player player, WorldObject source, WorldObject target)
        {
            // PY16 recipes
            var cookbook = DatabaseManager.World.GetCachedCookbook(source.WeenieClassId, target.WeenieClassId);
            if (cookbook != null)
                return cookbook.Recipe;

            // if none exists, try finding new recipe
            return GetNewRecipe(player, source, target);
        }

        public static void UseObjectOnTarget(Player player, WorldObject source, WorldObject target, bool confirmed = false)
        {
            if (player.IsBusy)
            {
                player.SendUseDoneEvent(WeenieError.YoureTooBusy);
                return;
            }

            var allowCraftInCombat = PropertyManager.GetBool("allow_combat_mode_crafting").Item;

            if (!allowCraftInCombat && player.CombatMode != CombatMode.NonCombat)
            {
                player.SendUseDoneEvent(WeenieError.YouMustBeInPeaceModeToTrade);
                return;
            }

            if (source == target)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"The {source.NameWithMaterial} cannot be combined with itself.", ChatMessageType.Craft));
                player.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(player.Session, $"You can't use the {source.NameWithMaterial} on itself."));
                player.SendUseDoneEvent();
                return;
            }

            if (Common.ConfigManager.Config.Server.WorldRuleset == Common.Ruleset.CustomDM && source.ItemType == ItemType.TinkeringMaterial && target.ItemType == ItemType.TinkeringMaterial)
            {
                AttemptCombineSalvageBags(player, source, target, confirmed);
                return;
            }

            var recipe = GetRecipe(player, source, target);

            if (recipe == null)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"The {source.NameWithMaterial} cannot be used on the {target.NameWithMaterial}.", ChatMessageType.Craft));
                player.SendUseDoneEvent();
                return;
            }

            // verify requirements
            if (!VerifyRequirements(recipe, player, source, target))
            {
                player.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
                return;
            }

            if (recipe.IsTinkering())
                log.Debug($"[TINKERING] {player.Name}.UseObjectOnTarget({source.NameWithMaterial}, {target.NameWithMaterial}) | Status: {(confirmed ? "" : "un")}confirmed");

            var percentSuccess = GetRecipeChance(player, source, target, recipe);

            if (percentSuccess == null)
            {
                player.SendUseDoneEvent();
                return;
            }

            var showDialog = HasDifficulty(recipe) && player.GetCharacterOption(CharacterOption.UseCraftingChanceOfSuccessDialog);

            if (!confirmed && player.LumAugSkilledCraft > 0)
                player.SendMessage($"Your Aura of the Craftman augmentation increased your skill by {player.LumAugSkilledCraft}!");

            var motionCommand = MotionCommand.ClapHands;

            var actionChain = new ActionChain();
            var nextUseTime = 0.0f;

            player.IsBusy = true;

            if (allowCraftInCombat && player.CombatMode != CombatMode.NonCombat)
            {
                // Drop out of combat mode.  This depends on the server property "allow_combat_mode_craft" being True.
                // If not, this action would have aborted due to not being in NonCombat mode.
                var stanceTime = player.SetCombatMode(CombatMode.NonCombat);
                actionChain.AddDelaySeconds(stanceTime);

                nextUseTime += stanceTime;
            }

            var motion = new Motion(player, motionCommand);
            var currentStance = player.CurrentMotionState.Stance; // expected to be MotionStance.NonCombat
            var clapTime = !confirmed ? Physics.Animation.MotionTable.GetAnimationLength(player.MotionTableId, currentStance, motionCommand) : 0.0f;

            if (!confirmed)
            {
                actionChain.AddAction(player, () => player.SendMotionAsCommands(motionCommand, currentStance));
                actionChain.AddDelaySeconds(clapTime);

                nextUseTime += clapTime;
            }

            if (showDialog && !confirmed)
            {
                if (Common.ConfigManager.Config.Server.WorldRuleset == Common.Ruleset.CustomDM && recipe.IsTinkering())
                    actionChain.AddAction(player, () => ShowDialog(player, source, target, null, percentSuccess.Value));
                else
                    actionChain.AddAction(player, () => ShowDialog(player, source, target, recipe, percentSuccess.Value));
                actionChain.AddAction(player, () => player.IsBusy = false);
                
                log.Info($"Player = {player.Name}; Tool = {source.Name}; Target = {target.Name}; Chance on confirmation dialog: {percentSuccess.Value}");
            }
            else
            {
                actionChain.AddAction(player, () => HandleRecipe(player, source, target, recipe, percentSuccess.Value));

                actionChain.AddAction(player, () =>
                {
                    if (!showDialog)
                        player.SendUseDoneEvent();

                    player.IsBusy = false;
                });
                
                log.Info($"Player = {player.Name}; Tool = {source.Name}; Target = {target.Name}; Chance after confirmation dialog: {percentSuccess.Value}");
            }

            actionChain.EnqueueChain();

            player.NextUseTime = DateTime.UtcNow.AddSeconds(nextUseTime);
        }

        public static bool HasDifficulty(Recipe recipe)
        {
            if (recipe.IsTinkering())
                return true;

            return recipe.Skill > 0 && recipe.Difficulty > 0;
        }

        public static double? GetRecipeChance(Player player, WorldObject source, WorldObject target, Recipe recipe)
        {
            if (recipe.IsTinkering())
                return GetTinkerChance(player, source, target, recipe);

            if (!HasDifficulty(recipe))
                return 1.0;

            var playerSkill = player.GetCreatureSkill((Skill)recipe.Skill);

            if (playerSkill == null)
            {
                // this shouldn't happen, but sanity check for unexpected nulls
                log.Warn($"RecipeManager.GetRecipeChance({player.Name}, {source.Name}, {target.Name}): recipe {recipe.Id} missing skill");
                return null;
            }

            // check for pre-MoA skill
            // convert into appropriate post-MoA skill
            // pre-MoA melee weapons: get highest melee weapons skill
            var newSkill = player.ConvertToMoASkill(playerSkill.Skill);

            playerSkill = player.GetCreatureSkill(newSkill);

            //Console.WriteLine("Required skill: " + skill.Skill);

            if (playerSkill.AdvancementClass < SkillAdvancementClass.Trained)
            {
                player.SendWeenieError(WeenieError.YouAreNotTrainedInThatTradeSkill);
                return null;
            }

            //Console.WriteLine("Skill difficulty: " + recipe.Recipe.Difficulty);

            var playerCurrentPlusLumAugSkilledCraft = playerSkill.Current + (uint)player.LumAugSkilledCraft;

            var successChance = SkillCheck.GetSkillChance(playerCurrentPlusLumAugSkilledCraft, recipe.Difficulty);

            return successChance;
        }

        public static double? GetTinkerChance(Player player, WorldObject tool, WorldObject target, Recipe recipe)
        {
            // calculate % success chance
            if (Common.ConfigManager.Config.Server.WorldRuleset != Common.Ruleset.CustomDM)
            {
                var toolWorkmanship = tool.Workmanship ?? 0;
                var itemWorkmanship = target.Workmanship ?? 0;

                var tinkeredCount = target.NumTimesTinkered;

                var materialType = tool.MaterialType ?? MaterialType.Unknown;

                // thanks to Endy's Tinkering Calculator for this formula!
                var attemptMod = TinkeringDifficulty[tinkeredCount];

                double successChance;
                var recipeSkill = (Skill)recipe.Skill;

                var skill = player.GetCreatureSkill(recipeSkill);

                // tinkering skill must be trained
                if (skill.AdvancementClass < SkillAdvancementClass.Trained && Common.ConfigManager.Config.Server.WorldRuleset != Common.Ruleset.CustomDM)
                {
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You are not trained in {skill.Skill.ToSentence()}.", ChatMessageType.Broadcast));
                    return null;
                }

                var salvageMod = GetMaterialMod(materialType);

                var workmanshipMod = 1.0f;
                if (toolWorkmanship >= itemWorkmanship)
                    workmanshipMod = 2.0f;

                var playerCurrentPlusLumAugSkilledCraft = skill.Current + (uint)player.LumAugSkilledCraft;

                var difficulty = (int)Math.Floor(((salvageMod * 5.0f) + (itemWorkmanship * salvageMod * 2.0f) - (toolWorkmanship * workmanshipMod * salvageMod / 5.0f)) * attemptMod);

                successChance = SkillCheck.GetSkillChance((int)playerCurrentPlusLumAugSkilledCraft, difficulty);

                // imbue: divide success by 3
                if (recipe.IsImbuing())
                {
                    successChance /= 3.0f;

                    if (player.AugmentationBonusImbueChance > 0)
                        successChance += player.AugmentationBonusImbueChance * 0.05f;
                }

                // todo: remove this once foolproof salvage recipes are updated
                if (foolproofTinkers.Contains((WeenieClassName)tool.WeenieClassId))
                    successChance = 1.0;

                return successChance;
            }
            else
            {
                return 1.0f;
            }
        }

        /// <summary>
        /// Returns the modifier for a bag of salvaging material
        /// </summary>
        public static float GetMaterialMod(MaterialType material)
        {
            switch (material)
            {
                case MaterialType.Gold:
                case MaterialType.Oak:
                    return 10.0f;

                case MaterialType.Alabaster:
                case MaterialType.ArmoredilloHide:
                case MaterialType.Brass:
                case MaterialType.Bronze:
                case MaterialType.Ceramic:
                case MaterialType.Granite:
                case MaterialType.Linen:
                case MaterialType.Marble:
                case MaterialType.Moonstone:
                case MaterialType.Opal:
                case MaterialType.Pine:
                case MaterialType.ReedSharkHide:
                case MaterialType.Velvet:
                case MaterialType.Wool:
                    return 11.0f;

                case MaterialType.Ebony:
                case MaterialType.GreenGarnet:
                case MaterialType.Iron:
                case MaterialType.Mahogany:
                case MaterialType.Porcelain:
                case MaterialType.Satin:
                case MaterialType.Steel:
                case MaterialType.Teak:
                    return 12.0f;

                case MaterialType.Bloodstone:
                case MaterialType.Carnelian:
                case MaterialType.Citrine:
                case MaterialType.Hematite:
                case MaterialType.LavenderJade:
                case MaterialType.Malachite:
                case MaterialType.RedJade:
                case MaterialType.RoseQuartz:
                    return 25.0f;

                default:
                    return 20.0f;
            }
        }

        /// <summary>
        /// Thanks to Endy's Tinkering Calculator for these values!
        /// </summary>
        public static List<float> TinkeringDifficulty = new List<float>()
        {
            // attempt #
            1.0f,   // 1
            1.1f,   // 2
            1.3f,   // 3
            1.6f,   // 4
            2.0f,   // 5
            2.5f,   // 6
            3.0f,   // 7
            3.5f,   // 8
            4.0f,   // 9
            4.5f    // 10
        };

        /// <summary>
        /// Returns TRUE if this material requires a skill check
        /// </summary>

        public static void ShowDialog(Player player, WorldObject source, WorldObject target, Recipe recipe, double successChance)
        {
            if(recipe == null)
            {
                string message;
                if (source.ItemType == ItemType.TinkeringMaterial && target.ItemType == ItemType.TinkeringMaterial)
                {
                    var materialName = GetMaterialName(source.MaterialType ?? 0);
                    message = $"Combining\n{source.Structure ?? 0} {materialName} of workmanship {source.Workmanship:0.00}\nand\n{target.Structure ?? 0} {materialName} of workmanship {target.Workmanship:0.00}\n";
                }
                else
                {
                    var materialName = GetMaterialName(source.MaterialType ?? 0);
                    message = $"Applying {materialName} of workmanship {source.Workmanship:0.00} to {target.NameWithMaterial}.\n";
                }

                if (!player.ConfirmationManager.EnqueueSend(new Confirmation_CraftInteration(player.Guid, source.Guid, target.Guid), message))
                {
                    player.SendUseDoneEvent(WeenieError.ConfirmationInProgress);
                    return;
                }
                player.SendUseDoneEvent();
                return;
            }

            var percent = successChance * 100;

            // retail messages:

            // You determine that you have a 100 percent chance to succeed.
            // You determine that you have a 99 percent chance to succeed.
            // You determine that you have a 38 percent chance to succeed. 5 percent is due to your augmentation.

            var floorMsg = $"You determine that you have a {percent.Round()} percent chance to succeed.";

            var numAugs = recipe.IsImbuing() ? player.AugmentationBonusImbueChance : 0;

            if (numAugs > 0)
                floorMsg += $"\n{numAugs * 5} percent is due to your augmentation.";

            if (!player.ConfirmationManager.EnqueueSend(new Confirmation_CraftInteration(player.Guid, source.Guid, target.Guid), floorMsg))
            {
                player.SendUseDoneEvent(WeenieError.ConfirmationInProgress);
                return;
            }

            if (PropertyManager.GetBool("craft_exact_msg").Item)
            {
                var exactMsg = $"You have a {(float)percent} percent chance of using {source.NameWithMaterial} on {target.NameWithMaterial}.";

                player.Session.Network.EnqueueSend(new GameMessageSystemChat(exactMsg, ChatMessageType.Craft));
            }
            player.SendUseDoneEvent();
        }

        public static void HandleRecipe(Player player, WorldObject source, WorldObject target, Recipe recipe, double successChance)
        {
            // re-verify
            if (!VerifyRequirements(recipe, player, source, target))
            {
                player.SendWeenieError(WeenieError.YouDoNotPassCraftingRequirements);
                return;
            }

            var roll = ThreadSafeRandom.Next(0.0f, 1.0f);
            var success = roll < successChance;
            log.Info($"Player = { player.Name}; Tool = { source.Name}; Target = { target.Name}; Chance = {successChance}; Roll = {roll}");

            if (recipe.IsImbuing())
            {
                player.ImbueAttempts++;
                if (success) player.ImbueSuccesses++;
            }

            var modified = CreateDestroyItems(player, recipe, source, target, successChance, success);

            if (modified != null)
            {
                if (modified.Contains(source.Guid.Full))
                    UpdateObj(player, source);

                if (modified.Contains(target.Guid.Full))
                    UpdateObj(player, target);
            }

            if (success && recipe.Skill > 0 && recipe.Difficulty > 0)
            {
                var skill = player.GetCreatureSkill((Skill)recipe.Skill);
                Proficiency.OnSuccessUse(player, skill, recipe.Difficulty);
            }
        }

        /// <summary>
        /// Sends an UpdateObj to the client for modified sources / targets
        /// </summary>
        private static void UpdateObj(Player player, WorldObject obj)
        {
            if (Debug)
                Console.WriteLine($"{player.Name}.UpdateObj({obj.Name})");

            player.EnqueueBroadcast(new GameMessageUpdateObject(obj));

            if (obj.CurrentWieldedLocation != null)
            {
                // retail possibly required sources / targets to be in the player's inventory,
                // and not equipped. this scenario might already be prevented beforehand in VerifyUse()
                player.EnqueueBroadcast(new GameMessageObjDescEvent(player));
                return;
            }

            // client automatically moves item to first slot in container
            // when an UpdateObject is sent. we must mimic this process on the server for persistance

            // only run this for items in the player's inventory
            // ie. skip for items on landblock, such as chorizite ore

            var invObj = player.FindObject(obj.Guid.Full, Player.SearchLocations.MyInventory);

            if (invObj != null)
                player.MoveItemToFirstContainerSlot(obj);
        }

        public static void Tinkering_ModifyItem(Player player, WorldObject tool, WorldObject target, bool incItemTinkered = true)
        {
            var materialType = tool.MaterialType ?? MaterialType.Unknown;
            var xtramodroll = ThreadSafeRandom.Next(1, 300); // helps determine which mod occurs
            var alamountlow = ThreadSafeRandom.Next(10, 30); // common base bonus AL
            var cfal = ThreadSafeRandom.Next(1, 25); // critical fail AL amount
            var modchance = ThreadSafeRandom.Next(1, 200);// the chance that a mod even will roll
            var resistroll = ThreadSafeRandom.Next(1, 187);
            var meleedmg = ThreadSafeRandom.Next(1, 4); // the roll for flat dmg for iron
            var cfaldmg = ThreadSafeRandom.Next(1, 3); //1min 3 max loss to dmg if critical fail
            var bowmoddmg = ThreadSafeRandom.Next(0.03f, 0.06f); // 1-2% bonus
            var bowmodfail = ThreadSafeRandom.Next(0.01f, 0.05f); // 1-5% failure amount
            var wandmodfail = ThreadSafeRandom.Next(0.001f, 0.007f); // .1% - .7% failure
            var wanddamage = ThreadSafeRandom.Next(0.002f, 0.007f); // .1% - .4% bonus
            var splitmodchance = ThreadSafeRandom.Next(1, 187);
            var retainlore = target.GetProperty(PropertyInt.ItemDifficulty); // ensure that the lore difficulty doesnt increase when spells are added.

            switch (materialType)
            {
                // armor tinkering
                case MaterialType.Steel:
                    if (modchance > 50) // 25% chance that an an extra mod bonus occurs
                    {
                        target.ArmorLevel += 65;
                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"No mod chance roll. Better luck next time. New Target AL {target.ArmorLevel}(+25)", ChatMessageType.Broadcast));
                    }
                    else
                    {
                        if (xtramodroll <= 200)
                        {
                            var alresult = 55 + alamountlow;
                            target.ArmorLevel += 55 + alamountlow;
                            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Not so lucky, but you also gained {alamountlow} extra AL! New Target AL {target.ArmorLevel}(+{alresult})", ChatMessageType.Broadcast));
                        }
                        else if (xtramodroll >= 201 && xtramodroll <= 251) // rolls for resistance mods.
                        {
                            if (resistroll >= 1 && resistroll <= 20) // pierce
                            {
                                target.ArmorLevel += 55;
                                target.ArmorModVsPierce = Math.Min((target.ArmorModVsPierce ?? 0) + 0.2f, 2.0f);
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Not so lucky, but you also gained an extra 20% Piercing resistance to your armor. {target.ArmorLevel}(+25)", ChatMessageType.Broadcast));
                            }
                            else if (resistroll >= 21 && resistroll <= 41) // slash
                            {
                                target.ArmorLevel += 55;
                                target.ArmorModVsSlash = Math.Min((target.ArmorModVsSlash ?? 0) + 0.2f, 2.0f);
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Not so lucky, but you also gained an extra 20% Slashing resistance to your armor. {target.ArmorLevel}(+25)", ChatMessageType.Broadcast));
                            }
                            else if (resistroll >= 42 && resistroll <= 62) // bludge
                            {
                                target.ArmorLevel += 55;
                                target.ArmorModVsBludgeon = Math.Min((target.ArmorModVsBludgeon ?? 0) + 0.2f, 2.0f);
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Not so lucky, but you also gained an extra 20% Bludgeon resistance to your armor. {target.ArmorLevel}(+25)", ChatMessageType.Broadcast));
                            }
                            else if (resistroll >= 63 && resistroll <= 83) // acid
                            {
                                target.ArmorLevel += 55;
                                target.ArmorModVsAcid = Math.Min((target.ArmorModVsAcid ?? 0) + 0.4f, 2.0f);
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Not so lucky, but you also gained an extra 40% Acid resistance to your armor. {target.ArmorLevel}(+25)", ChatMessageType.Broadcast));
                            }
                            else if (resistroll >= 84 && resistroll <= 104) // fire
                            {
                                target.ArmorLevel += 55;
                                target.ArmorModVsFire = Math.Min((target.ArmorModVsFire ?? 0) + 0.4f, 2.0f);
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Not so lucky, but you also gained an extra 40% Fire resistance to your armor. {target.ArmorLevel}(+25)", ChatMessageType.Broadcast));
                            }
                            else if (resistroll >= 105 && resistroll <= 125) // cold
                            {
                                target.ArmorLevel += 55;
                                target.ArmorModVsCold = Math.Min((target.ArmorModVsCold ?? 0) + 0.4f, 2.0f);
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Not so lucky, but you also gained an extra 40% Cold resistance to your armor. {target.ArmorLevel}(+25)", ChatMessageType.Broadcast));
                            }
                            else if (resistroll >= 126 && resistroll <= 146) // lightning
                            {
                                target.ArmorLevel += 55;
                                target.ArmorModVsElectric = Math.Min((target.ArmorModVsElectric ?? 0) + 0.4f, 2.0f);
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Not so lucky, but you also gained an extra 40% Lightning resistance to your armor. {target.ArmorLevel}(+25)", ChatMessageType.Broadcast));
                            }
                            else if (resistroll >= 147 && resistroll <= 167) // Nether
                            {
                                target.ArmorLevel += 55;
                                target.ArmorModVsNether = Math.Min((target.ArmorModVsNether ?? 0) + 0.4f, 2.0f);
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Not so lucky, but you also gained an extra 40% Nether resistance to your armor. {target.ArmorLevel}(+25)", ChatMessageType.Broadcast));
                            }
                        }
                        else if (xtramodroll >= 252 && xtramodroll <= 297)
                        {
                            target.ArmorLevel -= cfal;
                            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Critical failure! You lost {cfal} to your {target.NameWithMaterial} total AL. New Target AL {target.ArmorLevel}(-{cfal}).", ChatMessageType.Broadcast));
                        }

                        else if (xtramodroll == 298)
                        {
                            target.ArmorLevel += 80;
                            PlayerManager.BroadcastToAll(new GameMessageSystemChat($"{player.Name} Rolled a {xtramodroll}!! They just got super lucky applying steel to an item! Triple Value! New Target AL {target.ArmorLevel}(+80)", ChatMessageType.Broadcast));
                        }
                        else if (xtramodroll >= 299)
                        {
                            target.SetProperty(PropertyInt.Bonded, 1);
                            PlayerManager.BroadcastToAll(new GameMessageSystemChat($"{player.Name} Rolled a perfect {xtramodroll}!! They just got super lucky applying steel to their {target.NameWithMaterial}! The item is now bonded", ChatMessageType.Broadcast));
                        }
                    }
                    break;
                case MaterialType.Alabaster:
                    target.ArmorModVsPierce = Math.Min((target.ArmorModVsPierce ?? 0) + 0.2f, 2.0f);
                    break;
                case MaterialType.Bronze:
                    target.ArmorModVsSlash = Math.Min((target.ArmorModVsSlash ?? 0) + 0.2f, 2.0f);
                    break;
                case MaterialType.Marble:
                    target.ArmorModVsBludgeon = Math.Min((target.ArmorModVsBludgeon ?? 0) + 0.2f, 2.0f);
                    break;
                case MaterialType.ArmoredilloHide:
                    target.ArmorModVsAcid = Math.Min((target.ArmorModVsAcid ?? 0) + 0.4f, 2.0f);
                    break;
                case MaterialType.Ceramic:
                    target.ArmorModVsFire = Math.Min((target.ArmorModVsFire ?? 0) + 0.4f, 2.0f);
                    break;
                case MaterialType.Wool:
                    target.ArmorModVsCold = Math.Min((target.ArmorModVsCold ?? 0) + 0.4f, 2.0f);
                    break;
                case MaterialType.ReedSharkHide:
                    target.ArmorModVsElectric = Math.Min((target.ArmorModVsElectric ?? 0) + 0.4f, 2.0f);
                    break;
                case MaterialType.Peridot:
                    AddImbuedEffect(player, target, ImbuedEffectType.MeleeDefense);
                    break;
                case MaterialType.YellowTopaz:
                    AddImbuedEffect(player, target, ImbuedEffectType.MissileDefense);
                    break;
                case MaterialType.Zircon:
                    AddImbuedEffect(player, target, ImbuedEffectType.MagicDefense);
                    break;

                // item tinkering
                case MaterialType.Pine:
                    target.Value = (int)Math.Round((target.Value ?? 1) * 0.75f);
                    break;
                case MaterialType.Gold:
                    target.Value = (int)Math.Round((target.Value ?? 1) * 1.25f);
                    break;
                case MaterialType.Linen:
                    target.EncumbranceVal = (int)Math.Round((target.EncumbranceVal ?? 1) * 0.75f);
                    break;
                case MaterialType.Ivory:
                    // Recipe already handles this correctly
                    //target.SetProperty(PropertyInt.Attuned, 0);
                    break;
                case MaterialType.Leather:
                    target.Retained = true;
                    break;
                case MaterialType.Sandstone:
                    target.Retained = false;
                    break;
                case MaterialType.Moonstone:
                    target.ItemMaxMana += 500;
                    break;
                case MaterialType.Teak:
                    target.HeritageGroup = HeritageGroup.Aluvian;
                    break;
                case MaterialType.Ebony:
                    target.HeritageGroup = HeritageGroup.Gharundim;
                    break;
                case MaterialType.Porcelain:
                    target.HeritageGroup = HeritageGroup.Sho;
                    break;
                case MaterialType.Satin:
                    target.HeritageGroup = HeritageGroup.Viamontian;
                    break;


                case MaterialType.Silk:

                    // remove allegiance rank limit, increase item difficulty by spellcraft?
                    target.ItemAllegianceRankLimit = null;
                    target.ItemDifficulty = (target.ItemDifficulty ?? 0) + target.ItemSpellcraft;
                    break;

                // armatures / trinkets
                // these are handled in recipe mod
                case MaterialType.Amber:
                case MaterialType.Diamond:
                case MaterialType.GromnieHide:
                case MaterialType.Pyreal:
                case MaterialType.Ruby:
                case MaterialType.Sapphire:
                    return;

                // magic item tinkering

                case MaterialType.Sunstone:
                    AddImbuedEffect(player, target, ImbuedEffectType.ArmorRending);
                    break;
                case MaterialType.FireOpal:
                    AddImbuedEffect(player, target, ImbuedEffectType.CripplingBlow);
                    break;
                case MaterialType.BlackOpal:
                    AddImbuedEffect(player, target, ImbuedEffectType.CriticalStrike);
                    break;
                case MaterialType.Opal:
                    target.ManaConversionMod += 0.01f;
                    break;

                case MaterialType.GreenGarnet:

                    if (modchance <= 60) // 30% chance to even roll a mod to begin with
                    {
                        // basic roll + 0.4-0.9% dmg
                        if (xtramodroll <= 180) // 33.3%
                        {
                            if (target.GetProperty(PropertyInt.DamageType) == null)
                            {
                                if (splitmodchance >= 1 && splitmodchance <= 26)
                                {
                                    target.SetProperty(PropertyInt.DamageType, 1);
                                    target.SetProperty(PropertyFloat.ElementalDamageMod, 1.00);
                                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your {target.NameWithMaterial} has just been upgraded into a random elemental type. It now does bonus Slashing Damage", ChatMessageType.Broadcast));
                                }
                                else if (splitmodchance >= 27 && splitmodchance <= 53)
                                {
                                    target.SetProperty(PropertyInt.DamageType, 2);
                                    target.SetProperty(PropertyFloat.ElementalDamageMod, 1.00);
                                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your {target.NameWithMaterial} has just been upgraded into a random elemental type. It now does bonus Piercing Damage", ChatMessageType.Broadcast));
                                }
                                else if (splitmodchance >= 54 && splitmodchance <= 81)
                                {
                                    target.SetProperty(PropertyInt.DamageType, 4);
                                    target.SetProperty(PropertyFloat.ElementalDamageMod, 1.00);
                                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your {target.NameWithMaterial} has just been upgraded into a random elemental type. It now does bonus Bludgeon Damage", ChatMessageType.Broadcast));
                                }
                                else if (splitmodchance >= 82 && splitmodchance <= 108)
                                {
                                    target.SetProperty(PropertyInt.DamageType, 8);
                                    target.SetProperty(PropertyFloat.ElementalDamageMod, 1.00);
                                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your {target.NameWithMaterial} has just been upgraded into a random elemental type. It now does bonus Cold Damage", ChatMessageType.Broadcast));
                                }
                                else if (splitmodchance >= 109 && splitmodchance <= 136)
                                {
                                    target.SetProperty(PropertyInt.DamageType, 16);
                                    target.SetProperty(PropertyFloat.ElementalDamageMod, 1.00);
                                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your {target.NameWithMaterial} has just been upgraded into a random elemental type. It now does bonus Fire Damage", ChatMessageType.Broadcast));
                                }
                                else if (splitmodchance >= 137 && splitmodchance <= 164)
                                {
                                    target.SetProperty(PropertyInt.DamageType, 32);
                                    target.SetProperty(PropertyFloat.ElementalDamageMod, 1.00);
                                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your {target.NameWithMaterial} has just been upgraded into a random elemental type. It now does bonus Acid Damage", ChatMessageType.Broadcast));
                                }
                                else if (splitmodchance >= 165 && splitmodchance <= 187)
                                {
                                    target.SetProperty(PropertyInt.DamageType, 64);
                                    target.SetProperty(PropertyFloat.ElementalDamageMod, 1.00);
                                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your {target.NameWithMaterial} has just been upgraded into a random elemental type. It now does bonus Lightning Damage", ChatMessageType.Broadcast));
                                }
                            }// turns non elemental wands into elemental type.
                            target.ElementalDamageMod += 0.01f + wanddamage;
                            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Not so lucky, but you also gained {wanddamage:N3}% extra Elemental damage modifier!", ChatMessageType.Broadcast));
                        }//60%
                        // fail roll
                        else if (xtramodroll >= 181 && xtramodroll <= 190)
                        {
                            if (target.GetProperty(PropertyFloat.ElementalDamageMod) == null)
                            {
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Critical failure! The salvage applies poorly and does nothing!", ChatMessageType.Broadcast));
                            }
                            else
                            {
                                target.ElementalDamageMod -= wandmodfail;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Critical failure! You lost {wandmodfail:N3}% to your {target.NameWithMaterial}.", ChatMessageType.Broadcast));
                            }
                        }// 3%
                        // choose 1 mag d, melee d, Elemental Dmg.
                        else if (xtramodroll >= 191 && xtramodroll <= 209)
                        {
                            if (resistroll >= 1 && resistroll <= 60) // mag resist
                            {
                                target.WeaponMagicDefense += 0.01;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}, Not bad! You gained bonus Magic Defense {target.WeaponMagicDefense:N2}(+1%)", ChatMessageType.Broadcast));
                            }
                            else if (resistroll >= 61 && resistroll <= 120) // melee d
                            {
                                target.WeaponDefense += 0.01f;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. You got bonus Melee Defense {target.WeaponDefense:N2}(+1%)", ChatMessageType.Broadcast));
                            }
                            else if (resistroll >= 121 && resistroll <= 187) // elemental bonus + upgrade to
                            {
                                if (target.GetProperty(PropertyInt.DamageType) != null)
                                {
                                    target.ElementalDamageBonus += 2;
                                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. You got bonus Elemental Damage {target.ElementalDamageBonus:N0}(+2)", ChatMessageType.Broadcast));
                                }
                                else
                                {
                                    if (splitmodchance >= 1 && splitmodchance <= 26)
                                    {
                                        target.SetProperty(PropertyInt.DamageType, 1);
                                        target.SetProperty(PropertyInt.ElementalDamageBonus, 2);
                                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your missile weapon has just been upgraded into a random elemental weapon. It has gained +2 Slashing Damage", ChatMessageType.Broadcast));
                                    }
                                    else if (splitmodchance >= 27 && splitmodchance <= 53)
                                    {
                                        target.SetProperty(PropertyInt.DamageType, 2);
                                        target.SetProperty(PropertyInt.ElementalDamageBonus, 2);
                                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your missile weapon has just been upgraded into a random elemental weapon. It has gained +2 Piercing Damage", ChatMessageType.Broadcast));
                                    }
                                    else if (splitmodchance >= 54 && splitmodchance <= 81)
                                    {
                                        target.SetProperty(PropertyInt.DamageType, 4);
                                        target.SetProperty(PropertyInt.ElementalDamageBonus, 2);
                                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your missile weapon has just been upgraded into a random elemental weapon. It has gained +2 Bludgeon Damage", ChatMessageType.Broadcast));
                                    }
                                    else if (splitmodchance >= 82 && splitmodchance <= 108)
                                    {
                                        target.SetProperty(PropertyInt.DamageType, 8);
                                        target.SetProperty(PropertyInt.ElementalDamageBonus, 2);
                                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your missile weapon has just been upgraded into a random elemental weapon. It has gained +2 Cold Damage", ChatMessageType.Broadcast));
                                    }
                                    else if (splitmodchance >= 109 && splitmodchance <= 136)
                                    {
                                        target.SetProperty(PropertyInt.DamageType, 16);
                                        target.SetProperty(PropertyInt.ElementalDamageBonus, 2);
                                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your missile weapon has just been upgraded into a random elemental weapon. It has gained +2 Fire Damage", ChatMessageType.Broadcast));
                                    }
                                    else if (splitmodchance >= 137 && splitmodchance <= 164)
                                    {
                                        target.SetProperty(PropertyInt.DamageType, 32);
                                        target.SetProperty(PropertyInt.ElementalDamageBonus, 2);
                                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your missile weapon has just been upgraded into a random elemental weapon. It has gained +2 Acid Damage", ChatMessageType.Broadcast));
                                    }
                                    else if (splitmodchance >= 165 && splitmodchance <= 187)
                                    {
                                        target.SetProperty(PropertyInt.DamageType, 64);
                                        target.SetProperty(PropertyInt.ElementalDamageBonus, 2);
                                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your missile weapon has just been upgraded into a random elemental weapon. It has gained +2 Lightning Damage", ChatMessageType.Broadcast));
                                    }
                                    else
                                    {
                                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"BUG 1", ChatMessageType.Broadcast));
                                    }
                                }
                            }
                            else
                            {
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"BUG 3", ChatMessageType.Broadcast));
                            }
                        }// 6%
                         // Resistance Cleaving
                        else if (xtramodroll >= 210 && xtramodroll <= 234)
                        {
                            var dmgtype = target.GetProperty(PropertyInt.DamageType);

                            if (dmgtype == 1 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.SlashRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 1);
                                target.ElementalDamageMod += 0.01f;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You got lucky applying Green Garnet to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Slashing", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 2 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.PierceRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 2);
                                target.ElementalDamageMod += 0.01f;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You got lucky applying Green Garnet to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Piercing", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 4 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.BludgeonRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 4);
                                target.ElementalDamageMod += 0.01f;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You got lucky applying Green Garnet to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Bludgeon", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 8 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.ColdRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 8);
                                target.ElementalDamageMod += 0.01f;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You got lucky applying Green Garnet to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Cold", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 16 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.FireRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 16);
                                target.ElementalDamageMod += 0.01f;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You got lucky applying Green Garnet to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Fire", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 32 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.AcidRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 32);
                                target.ElementalDamageMod += 0.01f;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You got lucky applying Green Garnet to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Acid", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 64 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.ElectricRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 64);
                                target.ElementalDamageMod += 0.01f;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You got lucky applying Green Garnet to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Electric", ChatMessageType.Broadcast));
                            }
                            else if (target.GetProperty(PropertyInt.ResistanceModifierType) != null)
                            {
                                target.ElementalDamageMod += 0.01f;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"No mod chance roll. Better luck next time.", ChatMessageType.Broadcast));
                            }
                            else
                            {
                                target.ElementalDamageMod += 0.01f;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"No mod chance roll. Better luck next time.", ChatMessageType.Broadcast));
                            }
                        }// 8%
                        // Special Properties BS, CB, ETC
                        else if (xtramodroll == 235) // 2.6%
                        {
                            // biting Strike

                            if (resistroll >= 1 && resistroll <= 94 && target.GetProperty(PropertyFloat.CriticalFrequency) == null)
                            {
                                target.ElementalDamageMod += 0.01f;
                                target.SetProperty(PropertyFloat.CriticalFrequency, 0.10); // flat 10%?
                                PlayerManager.BroadcastToAll(new GameMessageSystemChat($"[TINKERING] {player.Name} just got super lucky applying Green Garnet to their {target.NameWithMaterial}! The item now has Biting Strike!", ChatMessageType.Broadcast));
                            }
                            // Critical Blow
                            else if (resistroll >= 95 && resistroll <= 187 && target.GetProperty(PropertyFloat.CriticalMultiplier) == null)
                            {
                                target.ElementalDamageMod += 0.01f;
                                target.SetProperty(PropertyFloat.CriticalMultiplier, 1.2); // is this really 1.2x? 20%?
                                PlayerManager.BroadcastToAll(new GameMessageSystemChat($"[TINKERING] {player.Name} just got super lucky applying Green Garnet to their {target.NameWithMaterial}! The item now has Crushing Blow!", ChatMessageType.Broadcast));
                            }
                            else
                            {
                                target.ElementalDamageMod += 0.01f;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"No mod chance roll. Better luck next time.", ChatMessageType.Broadcast));
                            }
                        }
                    }
                    break;
                // weapon tinkering

                case MaterialType.Iron:

                    if (modchance <= 70) // 38% chance to even roll a mod to begin with
                    {
                        // basic roll + 1-4 flat dmg
                        if (xtramodroll <= 180) // 33.3%
                        {
                            string variancenote = null;
                            if (target.Damage >= 80)
                            {
                                target.DamageVariance += 0.05;
                                variancenote = " You've also gained 5% variance.";
                                if (target.DamageVariance >= 1.0)
                                {
                                    target.DamageVariance = 0.9;
                                    variancenote = " Your weapon has reached maximum variance.";
                                }
                            }

                            var dmgresult = 3 + meleedmg;
                            target.Damage += 3 + meleedmg;
                            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Not so lucky, but you also gained {meleedmg} extra Flat damage! New Target Damage {target.Damage}(+{dmgresult}){variancenote}", ChatMessageType.Broadcast));

                        }// 60%
                        // fail roll
                        else if (xtramodroll >= 181 && xtramodroll <= 190)
                        {
                            target.Damage -= cfaldmg;
                            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Critical failure! You lost {cfaldmg} to your {target.NameWithMaterial} total Damage. New Target Damage {target.Damage}(-{cfaldmg}).", ChatMessageType.Broadcast));
                        }// 3%
                        // choose 1 mag d, melee d, attack mod.
                        else if (xtramodroll >= 191 && xtramodroll <= 209)
                        {
                            if (resistroll >= 1 && resistroll <= 60) // mag resist
                            {
                                target.Damage += 3;
                                target.WeaponMagicDefense += .05;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}, Not bad! You gained bonus Magic Defense {target.WeaponMagicDefense:N2}(+5%)", ChatMessageType.Broadcast));
                            }
                            else if (resistroll >= 61 && resistroll <= 120) // melee d
                            {
                                target.Damage += 3;
                                target.WeaponDefense += 0.01f;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. You got bonus Melee Defense {target.WeaponDefense:N2}(+1%)", ChatMessageType.Broadcast));
                            }
                            if (resistroll >= 121 && resistroll <= 187) // attack mod
                            {
                                target.Damage += 3;
                                target.WeaponOffense += 0.05f;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. You got bonus Attack Mod {target.WeaponOffense:N2}(+5%)", ChatMessageType.Broadcast));
                            }
                        }// 6%
                        // Resistance Cleaving
                        else if (xtramodroll >= 210 && xtramodroll <= 234)
                        {
                            var dmgtype = target.GetProperty(PropertyInt.DamageType);

                            if (dmgtype == 1 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.SlashRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 1);
                                target.Damage += 3;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying iron to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Slashing", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 2 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.PierceRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 2);
                                target.Damage += 3;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying iron to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Piercing", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 3 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.PierceRending) && !target.HasImbuedEffect(ImbuedEffectType.SlashRending))
                            {
                                var halfchance = ThreadSafeRandom.Next(1, 100);
                                if (halfchance >= 1 && halfchance <= 50)
                                {
                                    target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                    target.SetProperty(PropertyInt.ResistanceModifierType, 1);
                                    target.Damage += 3;
                                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying iron to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Slashing", ChatMessageType.Broadcast));
                                }
                                else if (halfchance >= 51 && halfchance <= 100)
                                {
                                    target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                    target.SetProperty(PropertyInt.ResistanceModifierType, 2);
                                    target.Damage += 3;
                                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying iron to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Piercing", ChatMessageType.Broadcast));
                                }
                            }
                            else if (dmgtype == 4 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.BludgeonRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 4);
                                target.Damage += 3;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying iron to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Bludgeon", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 8 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.ColdRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 8);
                                target.Damage += 3;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying iron to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Cold", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 16 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.FireRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 16);
                                target.Damage += 3;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying iron to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Fire", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 32 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.AcidRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 32);
                                target.Damage += 3;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying iron to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Acid", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 64 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.ElectricRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 64);
                                target.Damage += 3;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying iron to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Electric", ChatMessageType.Broadcast));
                            }
                            else if (target.GetProperty(PropertyInt.ResistanceModifierType) != null)
                            {
                                target.Damage += 3;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"No mod chance roll. Better luck next time. New Target Damage {target.Damage}(+3)", ChatMessageType.Broadcast));
                            }
                        }//8%
                         // Special Properties ArmorCleaving /*Cleave*/, BS, CB, ETC
                        else if (xtramodroll == 235) // 2.6%
                        {

                            int procs = 0;
                            if (target.GetProperty(PropertyBool.IgnoreMagicArmor) != null)
                                procs++;
                            if (target.GetProperty(PropertyBool.IgnoreMagicResist) != null)
                                procs++;
                            if (target.GetProperty(PropertyFloat.CriticalFrequency) != null)
                                procs++;
                            if (target.GetProperty(PropertyFloat.CriticalMultiplier) != null)
                                procs++;
                            if (target.GetProperty(PropertyInt.Cleaving) != null)
                                procs++;

                            //hollow properties
                            if (resistroll >= 1 && resistroll <= 30 && target.GetProperty(PropertyBool.IgnoreMagicArmor) == null)
                            {
                                target.Damage += 3;
                                target.SetProperty(PropertyBool.IgnoreMagicArmor, true);
                                PlayerManager.BroadcastToAll(new GameMessageSystemChat($"[TINKERING] {player.Name} just got super lucky applying iron to their {target.NameWithMaterial}! The item now ignores Partial armor values!", ChatMessageType.Broadcast));
                            }
                            else if (resistroll >= 31 && resistroll <= 61 && target.GetProperty(PropertyBool.IgnoreMagicResist) == null)
                            {
                                target.Damage += 3;
                                target.SetProperty(PropertyBool.IgnoreMagicResist, true);
                                PlayerManager.BroadcastToAll(new GameMessageSystemChat($"[TINKERING] {player.Name} just got super lucky applying iron to their {target.NameWithMaterial}! The item now has ignores partial magical protections!", ChatMessageType.Broadcast));
                            }
                            // biting Strike
                            if (resistroll >= 62 && resistroll <= 92 && target.GetProperty(PropertyFloat.CriticalFrequency) == null)
                            {
                                target.Damage += 3;
                                target.SetProperty(PropertyFloat.CriticalFrequency, 0.13);
                                PlayerManager.BroadcastToAll(new GameMessageSystemChat($"[TINKERING] {player.Name} just got super lucky applying iron to their {target.NameWithMaterial}! The item now has Biting Strike!", ChatMessageType.Broadcast));
                            }
                            // Critical Blow
                            else if (resistroll >= 93 && resistroll <= 123 && target.GetProperty(PropertyFloat.CriticalMultiplier) == null)
                            {
                                target.Damage += 3;
                                target.SetProperty(PropertyFloat.CriticalMultiplier, 3);
                                PlayerManager.BroadcastToAll(new GameMessageSystemChat($"[TINKERING] {player.Name} just got super lucky applying iron to their {target.NameWithMaterial}! The item now has Crushing Blow!", ChatMessageType.Broadcast));
                            }
                        }
                    }
                    break;

                case MaterialType.Mahogany:
                    // Always apply base 4% damage modification
                    target.DamageMod += 0.04f;

                    if (modchance <= 60) // 30% chance for additional bonus modifications
                    {
                        // basic roll + additional bonus
                        if (xtramodroll <= 180) // 33.3%
                        {
                            target.DamageMod += bowmoddmg;
                            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Not so lucky, but you also gained {bowmoddmg:N2} extra Bow Damage Modifier! New Target Damage Mod {target.DamageMod:N2}(+{0.04f + bowmoddmg:N2})", ChatMessageType.Broadcast));
                        }// 60%
                         // fail roll - lose some of the base bonus
                        else if (xtramodroll >= 181 && xtramodroll <= 190)
                        {
                            target.DamageMod -= bowmodfail;
                            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Rolled {xtramodroll}. Critical failure! You lost {bowmodfail:N2}% to your {target.NameWithMaterial}. New Target Damage Mod {target.DamageMod:N2}(+{0.04f - bowmodfail:N2})", ChatMessageType.Broadcast));
                        }// 3%
                         // choose 1 mag d, melee d, Elemental Dmg.
                        else if (xtramodroll >= 191 && xtramodroll <= 209)
                        {
                            if (resistroll >= 1 && resistroll <= 60) // mag resist
                            {
                                if (target.WeaponMagicDefense == null)
                                {
                                    target.SetProperty(PropertyFloat.WeaponMagicDefense, 1.01);
                                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}, Not bad! You gained bonus Magic Defense {target.WeaponMagicDefense:N2}(+1%) and base damage mod", ChatMessageType.Broadcast));
                                }
                                else
                                {
                                    target.WeaponMagicDefense += 0.01;
                                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}, Not bad! You gained bonus Magic Defense {target.WeaponMagicDefense:N2}(+1%) and base damage mod", ChatMessageType.Broadcast));
                                }
                            }
                            else if (resistroll >= 61 && resistroll <= 120) // melee d
                            {
                                target.WeaponDefense += 0.01f;
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. You got bonus Melee Defense {target.WeaponDefense:N2}(+1%) and base damage mod", ChatMessageType.Broadcast));
                            }
                            else if (resistroll >= 121 && resistroll <= 187) // elemental bonus + upgrade to
                            {
                                if (target.GetProperty(PropertyInt.DamageType) != null)
                                {
                                    target.ElementalDamageBonus += 3;
                                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. You got bonus Elemental Damage {target.ElementalDamageBonus:N0}(+3) and base damage mod", ChatMessageType.Broadcast));
                                }
                                else
                                {
                                    if (splitmodchance >= 1 && splitmodchance <= 26)
                                    {
                                        target.SetProperty(PropertyInt.DamageType, 1);
                                        target.SetProperty(PropertyInt.ElementalDamageBonus, 2);
                                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your missile weapon has just been upgraded into a random elemental weapon. It has gained +2 Slashing Damage and base damage mod", ChatMessageType.Broadcast));
                                    }
                                    else if (splitmodchance >= 27 && splitmodchance <= 53)
                                    {
                                        target.SetProperty(PropertyInt.DamageType, 2);
                                        target.SetProperty(PropertyInt.ElementalDamageBonus, 2);
                                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your missile weapon has just been upgraded into a random elemental weapon. It has gained +2 Piercing Damage and base damage mod", ChatMessageType.Broadcast));
                                    }
                                    else if (splitmodchance >= 54 && splitmodchance <= 81)
                                    {
                                        target.SetProperty(PropertyInt.DamageType, 4);
                                        target.SetProperty(PropertyInt.ElementalDamageBonus, 2);
                                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your missile weapon has just been upgraded into a random elemental weapon. It has gained +2 Bludgeon Damage and base damage mod", ChatMessageType.Broadcast));
                                    }
                                    else if (splitmodchance >= 82 && splitmodchance <= 108)
                                    {
                                        target.SetProperty(PropertyInt.DamageType, 8);
                                        target.SetProperty(PropertyInt.ElementalDamageBonus, 2);
                                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your missile weapon has just been upgraded into a random elemental weapon. It has gained +2 Cold Damage and base damage mod", ChatMessageType.Broadcast));
                                    }
                                    else if (splitmodchance >= 109 && splitmodchance <= 136)
                                    {
                                        target.SetProperty(PropertyInt.DamageType, 16);
                                        target.SetProperty(PropertyInt.ElementalDamageBonus, 2);
                                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your missile weapon has just been upgraded into a random elemental weapon. It has gained +2 Fire Damage and base damage mod", ChatMessageType.Broadcast));
                                    }
                                    else if (splitmodchance >= 137 && splitmodchance <= 164)
                                    {
                                        target.SetProperty(PropertyInt.DamageType, 32);
                                        target.SetProperty(PropertyInt.ElementalDamageBonus, 2);
                                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your missile weapon has just been upgraded into a random elemental weapon. It has gained +2 Acid Damage and base damage mod", ChatMessageType.Broadcast));
                                    }
                                    else if (splitmodchance >= 165 && splitmodchance <= 187)
                                    {
                                        target.SetProperty(PropertyInt.DamageType, 64);
                                        target.SetProperty(PropertyInt.ElementalDamageBonus, 2);
                                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You rolled {xtramodroll}. Your missile weapon has just been upgraded into a random elemental weapon. It has gained +2 Lightning Damage and base damage mod", ChatMessageType.Broadcast));
                                    }
                                    else
                                    {
                                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"BUG 1", ChatMessageType.Broadcast));
                                    }
                                }
                            }
                            else
                            {
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"BUG 3", ChatMessageType.Broadcast));
                            }
                        }// 6%
                         // Resistance Cleaving - keep existing logic but remove duplicate target.DamageMod += 0.04f;
                        else if (xtramodroll >= 210 && xtramodroll <= 234)
                        {
                            var dmgtype = target.GetProperty(PropertyInt.DamageType);

                            if (dmgtype == 1 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.SlashRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 1);
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying Mahogany to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Slashing and base damage mod", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 2 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.PierceRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 2);
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying Mahogany to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Piercing and base damage mod", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 3 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.PierceRending) && !target.HasImbuedEffect(ImbuedEffectType.SlashRending))
                            {
                                var halfchance = ThreadSafeRandom.Next(1, 100);
                                if (halfchance >= 1 && halfchance <= 50)
                                {
                                    target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                    target.SetProperty(PropertyInt.ResistanceModifierType, 1);
                                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying Mahogany to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Slashing and base damage mod", ChatMessageType.Broadcast));
                                }
                                else if (halfchance >= 51 && halfchance <= 100)
                                {
                                    target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                    target.SetProperty(PropertyInt.ResistanceModifierType, 2);
                                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying Mahogany to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Piercing and base damage mod", ChatMessageType.Broadcast));
                                }
                            }
                            else if (dmgtype == 4 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.BludgeonRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 4);
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying Mahogany to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Bludgeon and base damage mod", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 8 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.ColdRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 8);
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying Mahogany to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Cold and base damage mod", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 16 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.FireRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 16);
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying Mahogany to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Fire and base damage mod", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 32 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.AcidRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 32);
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying Mahogany to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Acid and base damage mod", ChatMessageType.Broadcast));
                            }
                            else if (dmgtype == 64 && target.GetProperty(PropertyInt.ResistanceModifierType) == null && !target.HasImbuedEffect(ImbuedEffectType.ElectricRending))
                            {
                                target.SetProperty(PropertyFloat.ResistanceModifier, 1);
                                target.SetProperty(PropertyInt.ResistanceModifierType, 64);
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[TINKERING] You just got lucky applying Mahogany to your {target.NameWithMaterial}! The item now has Resistance Cleaving: Electric and base damage mod", ChatMessageType.Broadcast));
                            }
                            else if (target.GetProperty(PropertyInt.ResistanceModifierType) != null)
                            {
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Base damage mod applied. New Target Damage Mod {target.DamageMod:N2}(+4%)", ChatMessageType.Broadcast));
                            }
                            else
                            {
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Base damage mod applied. New Target Damage Mod {target.DamageMod:N2}(+4%)", ChatMessageType.Broadcast));
                            }
                        }// 8%
                         // Special Properties Armor Cleave, BS, CB, ETC - keep existing logic but remove duplicate target.DamageMod += 0.04f;
                        else if (xtramodroll == 235) // 2.6%
                        {
                            //Armor rend
                            if (resistroll >= 1 && resistroll <= 61 && target.GetProperty(PropertyFloat.IgnoreArmor) != 1)
                            {
                                target.SetProperty(PropertyFloat.IgnoreArmor, 1);
                                PlayerManager.BroadcastToAll(new GameMessageSystemChat($"[TINKERING] {player.Name} just got super lucky applying Mahogony to their {target.NameWithMaterial}! The item now has Armor Cleaving and base damage mod!", ChatMessageType.Broadcast));
                            }
                            // biting Strike
                            else if (resistroll >= 62 && resistroll <= 124 && target.GetProperty(PropertyFloat.CriticalFrequency) == null)
                            {
                                target.SetProperty(PropertyFloat.CriticalFrequency, 0.15);
                                PlayerManager.BroadcastToAll(new GameMessageSystemChat($"[TINKERING] {player.Name} just got super lucky applying Mahogony to their {target.NameWithMaterial}! The item now has Biting Strike and base damage mod!", ChatMessageType.Broadcast));
                            }
                            // Critical Blow
                            else if (resistroll >= 125 && resistroll <= 187 && target.GetProperty(PropertyFloat.CriticalMultiplier) == null)
                            {
                                target.SetProperty(PropertyFloat.CriticalMultiplier, 3);
                                PlayerManager.BroadcastToAll(new GameMessageSystemChat($"[TINKERING] {player.Name} just got super lucky applying Mahogony to their {target.NameWithMaterial}! The item now has Crushing Blow and base damage mod!", ChatMessageType.Broadcast));
                            }
                        }
                    }
                    else
                    {
                        // 70% of the time - just base effect with message
                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Base damage mod applied. New Target Damage Mod {target.DamageMod:N2}(+4%)", ChatMessageType.Broadcast));
                    }
                    break;

                case MaterialType.Granite:
                    target.DamageVariance *= 0.8f;
                    break;
                case MaterialType.Oak:
                    target.WeaponTime = Math.Max(0, (target.WeaponTime ?? 0) - ThreadSafeRandom.Next(25, 70));
                    break;
                case MaterialType.Brass:
                    target.WeaponDefense += 0.05f;
                    break;
                case MaterialType.Velvet:
                    target.WeaponOffense += 0.05f;
                    break;

                // imbued effects
                case MaterialType.Emerald:
                    AddImbuedEffect(player, target, ImbuedEffectType.AcidRending);
                    break;
                case MaterialType.WhiteSapphire:
                    AddImbuedEffect(player, target, ImbuedEffectType.BludgeonRending);
                    break;
                case MaterialType.Aquamarine:
                    AddImbuedEffect(player, target, ImbuedEffectType.ColdRending);
                    break;
                case MaterialType.Jet:
                    AddImbuedEffect(player, target, ImbuedEffectType.ElectricRending);
                    break;
                case MaterialType.RedGarnet:
                    AddImbuedEffect(player, target, ImbuedEffectType.FireRending);
                    break;
                case MaterialType.BlackGarnet:
                    AddImbuedEffect(player, target, ImbuedEffectType.PierceRending);
                    break;
                case MaterialType.ImperialTopaz:
                    AddImbuedEffect(player, target, ImbuedEffectType.SlashRending);
                    break;

                default:
                    log.Error($"{player.Name}.RecipeManager.Tinkering_ModifyItem({tool.Name} ({tool.Guid}), {target.Name} ({target.Guid})) - Unknown material type: {materialType}");
                    return;
            }

            // increase # of times tinkered, if appropriate
            if (incItemTinkered)
            {
                target.NumTimesTinkered++;

                if (target.TinkerLog != null)
                    target.TinkerLog += ",";
                target.TinkerLog += (int)materialType;
            }
        }
        

        public static bool AddImbuedEffect(Player player, WorldObject target, ImbuedEffectType effect)
        {
            var imbuedEffects = GetImbuedEffects(target);

            if (imbuedEffects.HasFlag(effect))
                return false;     // already present

            imbuedEffects |= effect;

            if (target.GetProperty(PropertyInt.ImbuedEffect) == null)
                target.SetProperty(PropertyInt.ImbuedEffect, (int)effect);

            else if (target.GetProperty(PropertyInt.ImbuedEffect2) == null)
                target.SetProperty(PropertyInt.ImbuedEffect2, (int)effect);

            else if (target.GetProperty(PropertyInt.ImbuedEffect3) == null)
                target.SetProperty(PropertyInt.ImbuedEffect3, (int)effect);

            else if (target.GetProperty(PropertyInt.ImbuedEffect4) == null)
                target.SetProperty(PropertyInt.ImbuedEffect4, (int)effect);

            else if (target.GetProperty(PropertyInt.ImbuedEffect5) == null)
                target.SetProperty(PropertyInt.ImbuedEffect5, (int)effect);

            else
                return false;

            return true;
        }

        public static bool TryMutateNative(Player player, WorldObject source, WorldObject target, Recipe recipe, uint dataId)
        {
            // For tinkering operations, use the enhanced system
            if (recipe.IsTinkering() && Common.ConfigManager.Config.Server.WorldRuleset == Common.Ruleset.CustomDM)
            {
                // No skill check required for any material
                Tinkering_ModifyItem(player, source, target, true);

                HandleTinkerLog(source, target);

                return true;
            }
            
            // legacy method, unused by default
            switch (dataId)
            {
                // armor tinkering
                case 0x38000011:    // Steel
                    target.ArmorLevel += 60;
                    break;

                 // mutations apparently didn't cap to 2.0 here, clamps are applied in damage calculations though

                case 0x38000017:    // Alabaster
                    //target.ArmorModVsPierce = Math.Min((target.ArmorModVsPierce ?? 0) + 0.2f, 2.0f);
                    target.ArmorModVsPierce += 0.2f;
                    break;
                case 0x38000018:    // Bronze
                    //target.ArmorModVsSlash = Math.Min((target.ArmorModVsSlash ?? 0) + 0.2f, 2.0f);
                    target.ArmorModVsSlash += 0.2f;
                    break;
                case 0x38000013:    // Marble
                    //target.ArmorModVsBludgeon = Math.Min((target.ArmorModVsBludgeon ?? 0) + 0.2f, 2.0f);
                    target.ArmorModVsBludgeon += 0.2f;
                    break;
                case 0x38000012:    // ArmoredilloHide
                    //target.ArmorModVsAcid = Math.Min((target.ArmorModVsAcid ?? 0) + 0.4f, 2.0f);
                    target.ArmorModVsAcid += 0.4f;
                    break;
                case 0x38000016:    // Ceramic
                    //target.ArmorModVsFire = Math.Min((target.ArmorModVsFire ?? 0) + 0.4f, 2.0f);
                    target.ArmorModVsFire += 0.4f;
                    break;
                case 0x38000014:    // Wool
                    //target.ArmorModVsCold = Math.Min((target.ArmorModVsCold ?? 0) + 0.4f, 2.0f);
                    target.ArmorModVsCold += 0.4f;
                    break;
                case 0x38000015:    // ReedSharkHide
                    //target.ArmorModVsElectric = Math.Min((target.ArmorModVsElectric ?? 0) + 0.4f, 2.0f);
                    target.ArmorModVsElectric += 0.4f;
                    break;
                case 0x38000038:    // Peridot
                    //AddImbuedEffect(target, ImbuedEffectType.MeleeDefense);
                    target.ImbuedEffect = ImbuedEffectType.MeleeDefense;
                    break;
                case 0x38000039:    // YellowTopaz
                    //AddImbuedEffect(target, ImbuedEffectType.MissileDefense);
                    target.ImbuedEffect = ImbuedEffectType.MissileDefense;
                    break;
                case 0x38000037:    // Zircon
                    //AddImbuedEffect(target, ImbuedEffectType.MagicDefense);
                    target.ImbuedEffect = ImbuedEffectType.MagicDefense;
                    break;

                // item tinkering
                case 0x3800001E:    // Pine
                    //target.Value = (int)Math.Round((target.Value ?? 1) * 0.75f);
                    target.Value = (int?)(target.Value * 0.75f);
                    break;
                case 0x3800001F:    // Gold
                    //target.Value = (int)Math.Round((target.Value ?? 1) * 1.25f);
                    target.Value = (int?)(target.Value * 1.25f);
                    break;
                case 0x38000019:    // Linen
                    //target.EncumbranceVal = (int)Math.Round((target.EncumbranceVal ?? 1) * 0.75f);
                    target.EncumbranceVal = (int?)(target.EncumbranceVal * 0.75f);
                    break;
                // Ivory is handled purely in recipe mod?
                case 0x38000043:    // Leather
                    target.Retained = true;
                    break;
                case 0x3800004E:    // Sandstone: 43 -> 4E
                    target.Retained = false;
                    break;
                case 0x3800002F:    // Moonstone
                    target.ItemMaxMana += 500;
                    break;

                case 0x38000042:
                    switch (target.ItemHeritageGroupRestriction)
                    {
                        case "Aluvian":
                            target.HeritageGroup = HeritageGroup.Aluvian;
                            break;

                        case "Gharu'ndim":
                            target.HeritageGroup = HeritageGroup.Gharundim;
                            break;

                        case "Sho":
                            target.HeritageGroup = HeritageGroup.Sho;
                            break;

                        case "Viamontian":
                            target.HeritageGroup = HeritageGroup.Viamontian;
                            break;
                    }
                    break;

                case 0x38000035:    // Copper

                    // handled in requirements, only here for legacy support?
                    if (target.ItemSkillLimit != Skill.MissileDefense || target.ItemSkillLevelLimit == null)
                        return false;

                    // change activation requirement: missile defense -> melee defense
                    target.ItemSkillLimit = Skill.MeleeDefense;
                    target.ItemSkillLevelLimit = (int)(target.ItemSkillLevelLimit / 0.7f);
                    break;

                case 0x38000034:    // Silver

                    // handled in requirements, only here for legacy support?
                    if (target.ItemSkillLimit != Skill.MeleeDefense || target.ItemSkillLevelLimit == null)
                        return false;

                    // change activation requirement: melee defense -> missile defense
                    target.ItemSkillLimit = Skill.MissileDefense;
                    target.ItemSkillLevelLimit = (int)(target.ItemSkillLevelLimit * 0.7f);
                    break;

                case 0x38000036:    // Silk

                    // remove allegiance rank limit, set difficulty to spellcraft
                    target.ItemAllegianceRankLimit = null;
                    target.ItemDifficulty = target.ItemSpellcraft;
                    break;

                // armatures / trinkets
                // these are handled in recipe mod
                case 0x38000048:    // Amber
                case 0x38000049:    // Diamond
                case 0x38000050:    // GromnieHide
                case 0x38000051:    // Pyreal
                case 0x38000052:    // Ruby
                case 0x38000053:    // Sapphire
                    return false;

                // magic item tinkering

                case 0x38000025: // Sunstone
                    //AddImbuedEffect(target, ImbuedEffectType.ArmorRending);
                    target.ImbuedEffect = ImbuedEffectType.ArmorRending;
                    break;
                case 0x38000024: // FireOpal
                    //AddImbuedEffect(target, ImbuedEffectType.CripplingBlow);
                    target.ImbuedEffect = ImbuedEffectType.CripplingBlow;
                    break;
                case 0x38000023:    // BlackOpal
                    //AddImbuedEffect(target, ImbuedEffectType.CriticalStrike);
                    target.ImbuedEffect = ImbuedEffectType.CriticalStrike;
                    break;
                case 0x3800002E:    // Opal
                    //target.ManaConversionMod += 0.01f;
                    target.ManaConversionMod = (target.ManaConversionMod ?? 0.0f) + 0.01f;
                    break;
                case 0x3800004B:    // GreenGarnet: 44 -> 4B
                    target.ElementalDamageMod = (target.ElementalDamageMod ?? 0.0f) + 0.01f;     // + 1% vs. monsters, + 0.25% vs. players
                    break;

                case 0x38000041:
                    // these are handled in recipe mods already
                    // SmokeyQuartz
                    //AddSpell(player, target, SpellId.CANTRIPCOORDINATION1);
                    // RoseQuartz
                    //AddSpell(player, target, SpellId.CANTRIPQUICKNESS1);
                    // RedJade
                    //AddSpell(player, target, SpellId.CANTRIPHEALTHGAIN1);
                    // Malachite
                    //AddSpell(player, target, SpellId.WarriorsVigor);
                    // LavenderJade
                    //AddSpell(player, target, SpellId.CANTRIPMANAGAIN1);
                    // LapisLazuli
                    //AddSpell(player, target, SpellId.CANTRIPWILLPOWER1);
                    // Hematite
                    //AddSpell(player, target, SpellId.WarriorsVitality);
                    // Citrine
                    //AddSpell(player, target, SpellId.CANTRIPSTAMINAGAIN1);
                    // Carnelian
                    //AddSpell(player, target, SpellId.CANTRIPSTRENGTH1);
                    // Bloodstone
                    //AddSpell(player, target, SpellId.CANTRIPENDURANCE1);
                    // Azurite
                    //AddSpell(player, target, SpellId.WizardsIntellect);
                    // Agate
                    //AddSpell(player, target, SpellId.CANTRIPFOCUS1);

                    target.ImbuedEffect = ImbuedEffectType.Spellbook;
                    break;

                // weapon tinkering

                case 0x3800001A:    // Iron
                    target.Damage += 3;
                    break;
                case 0x3800001B:    // Mahogany
                    target.DamageMod += 0.12f;
                    break;
                case 0x3800001C:    // Granite / Lucky Rabbit's Foot
                    target.DamageVariance *= 0.8f;
                    break;
                case 0x3800001D:    // Oak
                    target.WeaponTime = Math.Max(0, (target.WeaponTime ?? 0) - 50);
                    break;
                case 0x38000020:    // Brass
                    target.WeaponDefense += 0.05f;
                    break;
                case 0x38000021:    // Velvet
                    target.WeaponOffense += 0.05f;
                    break;

                // only 1 imbue can be applied per piece of armor?
                case 0x3800003A:    // Emerald
                    //AddImbuedEffect(target, ImbuedEffectType.AcidRending);
                    target.ImbuedEffect = ImbuedEffectType.AcidRending;
                    break;
                case 0x3800003B:    // WhiteSapphire
                    //AddImbuedEffect(target, ImbuedEffectType.BludgeonRending);
                    target.ImbuedEffect = ImbuedEffectType.BludgeonRending;
                    break;
                case 0x3800003C:    // Aquamarine
                    //AddImbuedEffect(target, ImbuedEffectType.ColdRending);
                    target.ImbuedEffect = ImbuedEffectType.ColdRending;
                    break;
                case 0x3800003D:    // Jet
                    //AddImbuedEffect(target, ImbuedEffectType.ElectricRending);
                    target.ImbuedEffect = ImbuedEffectType.ElectricRending;
                    break;
                case 0x3800003E:    // RedGarnet
                    //AddImbuedEffect(target, ImbuedEffectType.FireRending);
                    target.ImbuedEffect = ImbuedEffectType.FireRending;
                    break;
                case 0x3800003F:    // BlackGarnet
                    //AddImbuedEffect(target, ImbuedEffectType.PierceRending);
                    target.ImbuedEffect = ImbuedEffectType.PierceRending;
                    break;
                case 0x38000040:    // ImperialTopaz
                    //AddImbuedEffect(target, ImbuedEffectType.SlashRending);
                    target.ImbuedEffect = ImbuedEffectType.SlashRending;
                    break;

                // addons

                case 0x3800000F:    // Stamps
                    target.IconOverlayId = target.IconOverlaySecondary;
                    break;

                case 0x38000046:    // Fetish of the Dark Idols

                    // shouldn't exist on player items, but just recreating original script here
                    if (target.ImbuedEffect >= ImbuedEffectType.IgnoreAllArmor)
                        target.ImbuedEffect = ImbuedEffectType.Undef;

                    target.ImbuedEffect |= ImbuedEffectType.IgnoreSomeMagicProjectileDamage;
                    //target.AbsorbMagicDamage = 0.25f;   // not in original mods / mutation?
                    break;

                case 0x39000000:    // Paragon Weapons
                    target.ItemMaxLevel = (target.ItemMaxLevel ?? 0) + 1;
                    target.ItemBaseXp = 2000000000;
                    target.ItemTotalXp = target.ItemTotalXp ?? 0;
                    break;

                default:
                    log.Error($"{player.Name}.RecipeManager.Tinkering_ModifyItem({source.Name} ({source.Guid}), {target.Name} ({target.Guid})) - unknown mutation id: {dataId:X8}");
                    return false;
            }

            if (incItemTinkered.Contains(dataId))
                HandleTinkerLog(source, target);

            return true;
        }

        // only needed for legacy method
        // ideally this wouldn't even be needed for the legacy method, and recipe.IsTinkering() would suffice
        // however, that would break for rare salvages, which have 0 difficulty and salvage_Type 0
        private static readonly HashSet<uint> incItemTinkered = new HashSet<uint>()
        {
            0x38000011, // Steel
            0x38000012, // Armoredillo Hide
            0x38000013, // Marble
            0x38000014, // Wool
            0x38000015, // Reedshark Hide
            0x38000016, // Ceramic
            0x38000017, // Alabaster
            0x38000018, // Bronze
            0x38000019, // Linen
            0x3800001A, // Iron
            0x3800001B, // Mahogany
            0x3800001C, // Granite
            0x3800001D, // Oak
            0x3800001E, // Pine
            0x3800001F, // Gold
            0x38000020, // Brass
            0x38000021, // Velvet
            0x38000023, // Black Opal
            0x38000024, // Fire Opal
            0x38000025, // Sunstone
            0x3800002E, // Opal
            0x3800002F, // Moonstone
            0x38000034, // Silver
            0x38000035, // Copper
            0x38000036, // Silk
            0x38000037, // Zircon
            0x38000038, // Peridot
            0x38000039, // Yellow Topaz
            0x3800003A, // Emerald
            0x3800003B, // White Sapphire
            0x3800003C, // Aquamarine
            0x3800003D, // Jet
            0x3800003E, // Red Garnet
            0x3800003F, // Black Garnet
            0x38000040, // Imperial Topaz
            0x38000041, // Cantrips
            0x38000042, // Heritage
            0x3800004B, // Green Garnet
        };

        public static void AddSpell(Player player, WorldObject target, SpellId spell, int difficulty = 25)
        {
            target.Biota.GetOrAddKnownSpell((int)spell, target.BiotaDatabaseLock, out _);
            target.ChangesDetected = true;

            if (difficulty != 0)
            {
                target.ItemSpellcraft = (target.ItemSpellcraft ?? 0) + difficulty;
                target.ItemDifficulty = (target.ItemDifficulty ?? 0) + difficulty;
            }
            if (target.UiEffects == null)
            {
                target.UiEffects = UiEffects.Magical;
                player.Session.Network.EnqueueSend(new GameMessagePublicUpdatePropertyInt(target, PropertyInt.UiEffects, (int)target.UiEffects));
            }
        }

        // derrick's input => output mappings
        public static Dictionary<ImbuedEffectType, uint> IconUnderlay = new Dictionary<ImbuedEffectType, uint>()
        {
            { ImbuedEffectType.ColdRending,     0x06003353 },
            { ImbuedEffectType.ElectricRending, 0x06003354 },
            { ImbuedEffectType.AcidRending,     0x06003355 },
            { ImbuedEffectType.ArmorRending,    0x06003356 },
            { ImbuedEffectType.CripplingBlow,   0x06003357 },
            { ImbuedEffectType.CriticalStrike,  0x06003358 },
            { ImbuedEffectType.FireRending,     0x06003359 },
            { ImbuedEffectType.BludgeonRending, 0x0600335a },
            { ImbuedEffectType.PierceRending,   0x0600335b },
            { ImbuedEffectType.SlashRending,    0x0600335c },
        };

        public static ImbuedEffectType GetImbuedEffects(WorldObject target)
        {
            var imbuedEffects = 0;

            imbuedEffects |= target.GetProperty(PropertyInt.ImbuedEffect) ?? 0;
            imbuedEffects |= target.GetProperty(PropertyInt.ImbuedEffect2) ?? 0;
            imbuedEffects |= target.GetProperty(PropertyInt.ImbuedEffect3) ?? 0;
            imbuedEffects |= target.GetProperty(PropertyInt.ImbuedEffect4) ?? 0;
            imbuedEffects |= target.GetProperty(PropertyInt.ImbuedEffect5) ?? 0;

            return (ImbuedEffectType)imbuedEffects;
        }

        public static bool VerifyRequirements(Recipe recipe, Player player, WorldObject source, WorldObject target)
        {
            if (!VerifyUse(player, source, target))
                return false;

            if (!player.VerifyGameplayMode(source, target))
            {
                player.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(player.Session, $"These items cannot be used, incompatible gameplay mode!"));
                return false;
            }

            if (Common.ConfigManager.Config.Server.WorldRuleset == Common.Ruleset.CustomDM && source.ItemType == ItemType.TinkeringMaterial && target.ItemType == ItemType.TinkeringMaterial)
            {
                // Salvage Combining
                if (source.Structure >= 100)
                {
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"The {source.NameWithMaterial} is already complete and cannot be combined.", ChatMessageType.Broadcast));
                    return false;
                }
                else if (target.Structure >= 100)
                {
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"The {target.NameWithMaterial} is already complete and cannot be combined.", ChatMessageType.Broadcast));
                    return false;
                }
                else if (source.MaterialType != target.MaterialType)
                {
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Only bags of the same material can be combined.", ChatMessageType.Broadcast));
                    return false;
                }

                return true;
            }

            if (!VerifyRequirements(recipe, player, target, RequirementType.Target)) return false;

            if (!VerifyRequirements(recipe, player, source, RequirementType.Source)) return false;

            if (!VerifyRequirements(recipe, player, player, RequirementType.Player)) return false;

            if (recipe.IsTinkering() && Common.ConfigManager.Config.Server.WorldRuleset == Common.Ruleset.CustomDM)
            {
                if (target.NumTimesTinkered >= target.GetMaxTinkerCount())
                {
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"The {target.NameWithMaterial} cannot be tinkered any further.", ChatMessageType.Broadcast));
                    return false;
                }

                if(Math.Floor(source.Workmanship ?? 0) < target.GetMinSalvageQualityForTinkering())
                {
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"The {source.NameWithMaterial} cannot be applied to {target.NameWithMaterial} because its workmanship is not high enough.", ChatMessageType.Broadcast));
                    return false;
                }

                if (target.TinkerLog != null)
                {
                    var tinkerType = (uint?)source.MaterialType ?? source.WeenieClassId;
                    var tinkers = target.TinkerLog.Split(",");
                    var tinkerCount = tinkers.Count(s => s == tinkerType.ToString());

                    if (tinkerCount > 0)
                    {
                        var material = "";
                        if (source.MaterialType == null)
                            material = source.NameWithMaterial;
                        else
                            material = GetMaterialName(source.MaterialType ?? 0);

                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"The {target.NameWithMaterial} has already been tinkered with {material}.", ChatMessageType.Broadcast));
                        return false;
                    }
                }
            }

            return true;
        }

        public static bool VerifyUse(Player player, WorldObject source, WorldObject target, bool blockWielded = false)
        {
            var usable = source.ItemUseable ?? Usable.Undef;

            if (usable == Usable.Undef)
            {
                log.Warn($"{player.Name}.RecipeManager.VerifyUse({source.Name} ({source.Guid}), {target.Name} ({target.Guid})) - source not usable, falling back on defaults");

                // re-verify
                if (player.FindObject(source.Guid.Full, Player.SearchLocations.MyInventory) == null)
                    return false;

                // almost always MyInventory, but sometimes can be applied to equipped
                if (!blockWielded && player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems) == null)
                    return false;
                else if (blockWielded && player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) == null)
                    return false;

                return true;
            }

            var sourceUse = usable.GetSourceFlags();
            var targetUse = usable.GetTargetFlags();

            return VerifyUse(player, source, sourceUse, blockWielded) && VerifyUse(player, target, targetUse, blockWielded);
        }

        public static bool VerifyUse(Player player, WorldObject obj, Usable usable, bool blockWielded = false)
        {
            var searchLocations = Player.SearchLocations.None;

            // TODO: figure out other Usable flags
            if (usable.HasFlag(Usable.Contained))
            {
                searchLocations |= Player.SearchLocations.MyInventory;
                if (!blockWielded)
                    searchLocations |= Player.SearchLocations.MyEquippedItems;
            }
            if (!blockWielded && usable.HasFlag(Usable.Wielded))
                searchLocations |= Player.SearchLocations.MyEquippedItems;
            if (usable.HasFlag(Usable.Remote))
                searchLocations |= Player.SearchLocations.LocationsICanMove;    // TODO: moveto for this type

            return player.FindObject(obj.Guid.Full, searchLocations) != null;
        }

        public static bool Debug = false;

        public static bool VerifyRequirements(Recipe recipe, Player player, WorldObject obj, RequirementType reqType)
        {
            var boolReqs = recipe.RecipeRequirementsBool.Where(i => i.Index == (int)reqType).ToList();
            var intReqs = recipe.RecipeRequirementsInt.Where(i => i.Index == (int)reqType).ToList();
            var floatReqs = recipe.RecipeRequirementsFloat.Where(i => i.Index == (int)reqType).ToList();
            var strReqs = recipe.RecipeRequirementsString.Where(i => i.Index == (int)reqType).ToList();
            var iidReqs = recipe.RecipeRequirementsIID.Where(i => i.Index == (int)reqType).ToList();
            var didReqs = recipe.RecipeRequirementsDID.Where(i => i.Index == (int)reqType).ToList();

            var totalReqs = boolReqs.Count + intReqs.Count + floatReqs.Count + strReqs.Count + iidReqs.Count + didReqs.Count;

            if (Debug && totalReqs > 0)
                Console.WriteLine($"{reqType} Requirements: {totalReqs}");

            foreach (var requirement in boolReqs)
            {
                bool? value = obj.GetProperty((PropertyBool)requirement.Stat);
                double? normalized = value != null ? (double?)Convert.ToDouble(value.Value) : null;

                if (Debug)
                    Console.WriteLine($"PropertyBool.{(PropertyBool)requirement.Stat} {(CompareType)requirement.Enum} {requirement.Value}, current: {value}");

                if (!VerifyRequirement(player, (CompareType)requirement.Enum, normalized, Convert.ToDouble(requirement.Value), requirement.Message))
                    return false;
            }

            foreach (var requirement in intReqs)
            {
                int? value = obj.GetProperty((PropertyInt)requirement.Stat);
                double? normalized = value != null ? (double?)Convert.ToDouble(value.Value) : null;

                if (Debug)
                    Console.WriteLine($"PropertyInt.{(PropertyInt)requirement.Stat} {(CompareType)requirement.Enum} {requirement.Value}, current: {value}");

                if (!VerifyRequirement(player, (CompareType)requirement.Enum, normalized, Convert.ToDouble(requirement.Value), requirement.Message))
                    return false;
            }

            foreach (var requirement in floatReqs)
            {
                double? value = obj.GetProperty((PropertyFloat)requirement.Stat);

                if (Debug)
                    Console.WriteLine($"PropertyFloat.{(PropertyFloat)requirement.Stat} {(CompareType)requirement.Enum} {requirement.Value}, current: {value}");

                if (!VerifyRequirement(player, (CompareType)requirement.Enum, value, requirement.Value, requirement.Message))
                    return false;
            }

            foreach (var requirement in strReqs)
            {
                string value = obj.GetProperty((PropertyString)requirement.Stat);

                if (Debug)
                    Console.WriteLine($"PropertyString.{(PropertyString)requirement.Stat} {(CompareType)requirement.Enum} {requirement.Value}, current: {value}");

                if (!VerifyRequirement(player, (CompareType)requirement.Enum, value, requirement.Value, requirement.Message))
                    return false;
            }

            foreach (var requirement in iidReqs)
            {
                uint? value = obj.GetProperty((PropertyInstanceId)requirement.Stat);
                double? normalized = value != null ? (double?)Convert.ToDouble(value.Value) : null;

                if (Debug)
                    Console.WriteLine($"PropertyInstanceId.{(PropertyInstanceId)requirement.Stat} {(CompareType)requirement.Enum} {requirement.Value}, current: {value}");

                if (!VerifyRequirement(player, (CompareType)requirement.Enum, normalized, Convert.ToDouble(requirement.Value), requirement.Message))
                    return false;
            }

            foreach (var requirement in didReqs)
            {
                uint? value = obj.GetProperty((PropertyDataId)requirement.Stat);
                double? normalized = value != null ? (double?)Convert.ToDouble(value.Value) : null;

                if (Debug)
                    Console.WriteLine($"PropertyDataId.{(PropertyDataId)requirement.Stat} {(CompareType)requirement.Enum} {requirement.Value}, current: {value}");

                if (!VerifyRequirement(player, (CompareType)requirement.Enum, normalized, Convert.ToDouble(requirement.Value), requirement.Message))
                    return false;
            }

            if (Debug && totalReqs > 0)
                Console.WriteLine($"-----");

            return true;
        }

        public static bool VerifyRequirement(Player player, CompareType compareType, double? prop, double val, string failMsg)
        {
            var success = true;

            switch (compareType)
            {
                case CompareType.GreaterThan:
                    if ((prop ?? 0) > val)
                        success = false;
                    break;

                case CompareType.LessThanEqual:
                    if ((prop ?? 0) <= val)
                        success = false;
                    break;

                case CompareType.LessThan:
                    if ((prop ?? 0) < val)
                        success = false;
                    break;

                case CompareType.GreaterThanEqual:
                    if ((prop ?? 0) >= val)
                        success = false;
                    break;

                case CompareType.NotEqual:
                    if ((prop ?? 0) != val)
                        success = false;
                    break;

                case CompareType.NotEqualNotExist:
                    if (prop == null || prop.Value != val)
                        success = false;
                    break;

                case CompareType.Equal:
                    if ((prop ?? 0) == val)
                        success = false;
                    break;

                case CompareType.NotExist:
                    if (prop == null)
                        success = false;
                    break;

                case CompareType.Exist:
                    if (prop != null)
                        success = false;
                    break;

                case CompareType.NotHasBits:
                    if (((int)(prop ?? 0) & (int)val) == 0)
                        success = false;
                    break;

                case CompareType.HasBits:
                    if (((int)(prop ?? 0) & (int)val) == (int)val)
                        success = false;
                    break;
            }

            if (!success)
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(failMsg, ChatMessageType.Craft));

            return success;
        }

        public static bool VerifyRequirement(Player player, CompareType compareType, string prop, string val, string failMsg)
        {
            var success = true;

            switch (compareType)
            {
                case CompareType.NotEqual:
                    if (!(prop ?? "").Equals(val))
                        success = false;
                    break;

                case CompareType.NotEqualNotExist:
                    if (prop == null || !prop.Equals(val))
                        success = false;
                    break;

                case CompareType.Equal:
                    if ((prop ?? "").Equals(val))
                        success = false;
                    break;

                case CompareType.NotExist:
                    if (prop == null)
                        success = false;
                    break;

                case CompareType.Exist:
                    if (prop != null)
                        success = false;
                    break;
            }
            if (!success)
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(failMsg, ChatMessageType.Craft));

            return success;
        }

        /// <summary>
        /// Returns a list of object guids that have been modified
        /// </summary>
        public static HashSet<uint> CreateDestroyItems(Player player, Recipe recipe, WorldObject source, WorldObject target, double successChance, bool success)
        {
            var destroyTargetChance = success ? recipe.SuccessDestroyTargetChance : recipe.FailDestroyTargetChance;
            var destroySourceChance = success ? recipe.SuccessDestroySourceChance : recipe.FailDestroySourceChance;

            var destroyTarget = ThreadSafeRandom.Next(0.0f, 1.0f) < destroyTargetChance;
            var destroySource = ThreadSafeRandom.Next(0.0f, 1.0f) < destroySourceChance;

            var createItem = success ? recipe.SuccessWCID : recipe.FailWCID;
            var createAmount = success ? recipe.SuccessAmount : recipe.FailAmount;

            if (createItem > 0 && DatabaseManager.World.GetCachedWeenie(createItem) == null)
            {
                log.Error($"RecipeManager.CreateDestroyItems: Recipe.Id({recipe.Id}) couldn't find {(success ? "Success" : "Fail")}WCID {createItem} in database.");
                player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.CraftGeneralErrorUiMsg));
                return null;
            }

            if (destroyTarget)
            {
                var destroyTargetAmount = success ? recipe.SuccessDestroyTargetAmount : recipe.FailDestroyTargetAmount;
                var destroyTargetMessage = success ? recipe.SuccessDestroyTargetMessage : recipe.FailDestroyTargetMessage;

                if (recipe.IsTinkering() && target.WeenieType == WeenieType.Missile)
                    destroyTargetAmount = (uint)(target.StackSize ?? 1); // Thrown weapons tinkering should destroy the whole stack on failure.

                DestroyItem(player, recipe, target, destroyTargetAmount, destroyTargetMessage);
            }

            if (destroySource)
            {
                var destroySourceAmount = success ? recipe.SuccessDestroySourceAmount : recipe.FailDestroySourceAmount;
                var destroySourceMessage = success ? recipe.SuccessDestroySourceMessage : recipe.FailDestroySourceMessage;

                DestroyItem(player, recipe, source, destroySourceAmount, destroySourceMessage);
            }

            WorldObject result = null;

            if (createItem > 0)
            {
                result = CreateItem(player, createItem, createAmount);

                if (destroyTarget && result != null && target.ExtraSpellsList != null)
                {
                    // Transfer spells to the new item.
                    var spells = target.ExtraSpellsList.Split(",");

                    foreach (string spellString in spells)
                    {
                        if (uint.TryParse(spellString, out var spellId))
                            SpellTransferScroll.InjectSpell(spellId, result);
                    }

                    player.EnqueueBroadcast(new GameMessageUpdateObject(result));
                }
            }

            var modified = ModifyItem(player, recipe, source, target, result, success);

            // New method need for tracking tool and type
            if (result != null && QuestItemMutations.IsToolValidForQuestMutation(source.WeenieClassId))
            {
                var mutationResult = result.MutateQuestItem();

                if (!string.IsNullOrEmpty(mutationResult))
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(mutationResult, ChatMessageType.System));
            }

            if (result != null)
                result.MutateQuestItem();

            // broadcast different messages based on recipe type
            if (!recipe.IsTinkering())
            {
                var message = success ? recipe.SuccessMessage : recipe.FailMessage;

                player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Craft));

                log.Debug($"[CRAFTING] {player.Name} used {source.NameWithMaterial} on {target.NameWithMaterial} {(success ? "" : "un")}successfully. {(destroySource ? $"| {source.NameWithMaterial} was destroyed " : "")}{(destroyTarget ? $"| {target.NameWithMaterial} was destroyed " : "")}| {message}");
            }
            else
                BroadcastTinkering(player, source, target, successChance, success);

            return modified;
        }

        public static void BroadcastTinkering(Player player, WorldObject tool, WorldObject target, double chance, bool success)
        {
            var sourceName = Regex.Replace(tool.NameWithMaterial, @" \(\d+\)$", "");

            // send local broadcast
            if (success)
                player.EnqueueBroadcast(new GameMessageSystemChat($"{player.Name} successfully applies the {sourceName} (workmanship {(tool.Workmanship ?? 0):#.00}) to the {target.NameWithMaterial}.", ChatMessageType.Craft), WorldObject.LocalBroadcastRange, ChatMessageType.Craft);
            else
                player.EnqueueBroadcast(new GameMessageSystemChat($"{player.Name} fails to apply the {sourceName} (workmanship {(tool.Workmanship ?? 0):#.00}) to the {target.NameWithMaterial}. The target is destroyed.", ChatMessageType.Craft), WorldObject.LocalBroadcastRange, ChatMessageType.Craft);

            log.Debug($"[TINKERING] {player.Name} {(success ? "successfully applies" : "fails to apply")} the {sourceName} (workmanship {(tool.Workmanship ?? 0):#.00}) to the {target.NameWithMaterial}.{(!success ? " The target is destroyed." : "")} | Chance: {chance}");
        }

        public static WorldObject CreateItem(Player player, uint wcid, uint amount)
        {
            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);

            if (wo == null)
            {
                log.Warn($"RecipeManager.CreateItem({player.Name}, {wcid}, {amount}): failed to create {wcid}");
                return null;
            }

            if (amount > 1)
                wo.SetStackSize((int)amount);

            player.TryCreateInInventoryWithNetworking(wo, out _, true);
            return wo;
        }

        public static void DestroyItem(Player player, Recipe recipe, WorldObject item, uint amount, string msg)
        {
            if (item.OwnerId == player.Guid.Full || player.GetInventoryItem(item.Guid) != null)
            {
                if (!player.TryConsumeFromInventoryWithNetworking(item, (int)amount))
                    log.Warn($"RecipeManager.DestroyItem({player.Name}, {item.Name}, {amount}, {msg}): failed to remove {item.Name}");
            }
            else if (item.WielderId == player.Guid.Full)
            {
                if (!player.TryDequipObjectWithNetworking(item.Guid, out _, Player.DequipObjectAction.ConsumeItem))
                    log.Warn($"RecipeManager.DestroyItem({player.Name}, {item.Name}, {amount}, {msg}): failed to remove {item.Name}");
            }
            else
            {
                item.Destroy();
            }
            if (!string.IsNullOrEmpty(msg))
            {
                var destroyMessage = new GameMessageSystemChat(msg, ChatMessageType.Craft);
                player.Session.Network.EnqueueSend(destroyMessage);
            }
        }

        public static WorldObject GetSourceMod(RecipeSourceType sourceType, Player player, WorldObject source)
        {
            switch (sourceType)
            {
                case RecipeSourceType.Player:
                    return player;
                case RecipeSourceType.Source:
                    return source;
            }
            log.Warn($"RecipeManager.GetSourceMod({sourceType}, {player.Name}, {source.Name}) - unknown source type");
            return null;
        }

        public static WorldObject GetTargetMod(ModificationType type, WorldObject source, WorldObject target, Player player, WorldObject result)
        {
            switch (type)
            {
                case ModificationType.SuccessSource:
                case ModificationType.FailureSource:
                    return source;

                default:
                    return target;

                case ModificationType.SuccessPlayer:
                case ModificationType.FailurePlayer:
                    return player;

                case ModificationType.SuccessResult:
                case ModificationType.FailureResult:
                    return result ?? target;
            }
        }

        /// <summary>
        /// Returns a list of object guids that have been modified
        /// </summary>
        public static HashSet<uint> ModifyItem(Player player, Recipe recipe, WorldObject source, WorldObject target, WorldObject result, bool success)
        {
            var modified = new HashSet<uint>();

            foreach (var mod in recipe.RecipeMod)
            {
                if (mod.ExecutesOnSuccess != success)
                    continue;

                // adjust vitals
                if (mod.Health != 0)
                    ModifyVital(player, PropertyAttribute2nd.Health, mod.Health);

                if (mod.Stamina != 0)
                    ModifyVital(player, PropertyAttribute2nd.Stamina, mod.Stamina);

                if (mod.Mana != 0)
                    ModifyVital(player, PropertyAttribute2nd.Mana, mod.Mana);

                // apply type mods
                foreach (var boolMod in mod.RecipeModsBool)
                    ModifyBool(player, boolMod, source, target, result, modified);

                foreach (var intMod in mod.RecipeModsInt)
                    ModifyInt(player, intMod, source, target, result, modified);

                foreach (var floatMod in mod.RecipeModsFloat)
                    ModifyFloat(player, floatMod, source, target, result, modified);

                foreach (var stringMod in mod.RecipeModsString)
                    ModifyString(player, stringMod, source, target, result, modified);

                foreach (var iidMod in mod.RecipeModsIID)
                    ModifyInstanceID(player, iidMod, source, target, result, modified);

                foreach (var didMod in mod.RecipeModsDID)
                    ModifyDataID(player, didMod, source, target, result, modified);

                if (mod.WeenieClassId != 0)
                    ModifyWeenieClassId(target, (uint)mod.WeenieClassId, modified);

                // run mutation script, if applicable
                if (mod.DataId != 0)
                    TryMutate(player, source, target, recipe, (uint)mod.DataId, modified);
            }

            return modified;
        }

        private static void ModifyVital(Player player, PropertyAttribute2nd attribute2nd, int value)
        {
            var vital = player.GetCreatureVital(attribute2nd);

            var vitalChange = (uint)Math.Abs(player.UpdateVitalDelta(vital, value));

            if (attribute2nd == PropertyAttribute2nd.Health)
            {
                if (value >= 0)
                    player.DamageHistory.OnHeal(vitalChange);
                else
                    player.DamageHistory.Add(player, DamageType.Health, vitalChange);

                if (player.Health.Current <= 0)
                {
                    // should this be possible?
                    //var lastDamager = player != null ? new DamageHistoryInfo(player) : null;
                    var lastDamager = new DamageHistoryInfo(player);

                    player.OnDeath(lastDamager, DamageType.Health, false);
                    player.Die();
                }
            }
        }

        public static void ModifyBool(Player player, RecipeModsBool boolMod, WorldObject source, WorldObject target, WorldObject result, HashSet<uint> modified)
        {
            var op = (ModificationOperation)boolMod.Enum;
            var prop = (PropertyBool)boolMod.Stat;
            var value = boolMod.Value;

            var targetMod = GetTargetMod((ModificationType)boolMod.Index, source, target, player, result);

            // always SetValue?
            if (op != ModificationOperation.SetValue)
            {
                log.Warn($"RecipeManager.ModifyBool({source.Name}, {target.Name}): unhandled operation {op}");
                return;
            }
            player.UpdateProperty(targetMod, prop, value);
            modified.Add(targetMod.Guid.Full);

            if (Debug)
                Console.WriteLine($"{targetMod.Name}.SetProperty({prop}, {value}) - {op}");
        }

        public static void ModifyInt(Player player, RecipeModsInt intMod, WorldObject source, WorldObject target, WorldObject result, HashSet<uint> modified)
        {
            var op = (ModificationOperation)intMod.Enum;
            var prop = (PropertyInt)intMod.Stat;
            var value = intMod.Value;

            var sourceMod = GetSourceMod((RecipeSourceType)intMod.Source, player, source);
            var targetMod = GetTargetMod((ModificationType)intMod.Index, source, target, player, result);

            switch (op)
            {
                case ModificationOperation.SetValue:
                    player.UpdateProperty(targetMod, prop, value);
                    modified.Add(targetMod.Guid.Full);
                    if (Debug) Console.WriteLine($"{targetMod.Name}.SetProperty({prop}, {value}) - {op}");
                    break;
                case ModificationOperation.Add:
                    player.UpdateProperty(targetMod, prop, (targetMod.GetProperty(prop) ?? 0) + value);
                    modified.Add(targetMod.Guid.Full);
                    if (Debug) Console.WriteLine($"{targetMod.Name}.IncProperty({prop}, {value}) - {op}");
                    break;
                case ModificationOperation.CopyFromSourceToTarget:
                    player.UpdateProperty(target, prop, sourceMod.GetProperty(prop) ?? 0);
                    modified.Add(target.Guid.Full);
                    if (Debug) Console.WriteLine($"{target.Name}.SetProperty({prop}, {sourceMod.GetProperty(prop) ?? 0}) - {op}");
                    break;
                case ModificationOperation.CopyFromSourceToResult:
                    player.UpdateProperty(result, prop, player.GetProperty(prop) ?? 0);
                    modified.Add(result.Guid.Full);
                    if (Debug) Console.WriteLine($"{result.Name}.SetProperty({prop}, {player.GetProperty(prop) ?? 0}) - {op}");
                    break;
                case ModificationOperation.AddSpell:
                    targetMod.Biota.GetOrAddKnownSpell(intMod.Stat, target.BiotaDatabaseLock, out var added);
                    modified.Add(targetMod.Guid.Full);
                    if (added)
                        targetMod.ChangesDetected = true;
                    if (Debug) Console.WriteLine($"{targetMod.Name}.AddSpell({intMod.Stat}) - {op}");
                    break;
                case ModificationOperation.SetBitsOn:
                    var bits = targetMod.GetProperty(prop) ?? 0;
                    bits |= value;
                    player.UpdateProperty(targetMod, prop, bits);
                    modified.Add(targetMod.Guid.Full);
                    if (Debug) Console.WriteLine($"{targetMod.Name}.SetProperty({prop}, 0x{bits:X}) - {op}");
                    break;
                case ModificationOperation.SetBitsOff:
                    bits = targetMod.GetProperty(prop) ?? 0;
                    bits &= ~value;
                    player.UpdateProperty(targetMod, prop, bits);
                    modified.Add(targetMod.Guid.Full);
                    if (Debug) Console.WriteLine($"{targetMod.Name}.SetProperty({prop}, 0x{bits:X}) - {op}");
                    break;
                default:
                    log.Warn($"RecipeManager.ModifyInt({source.Name}, {target.Name}): unhandled operation {op}");
                    break;
            }
        }

        public static void ModifyFloat(Player player, RecipeModsFloat floatMod, WorldObject source, WorldObject target, WorldObject result, HashSet<uint> modified)
        {
            var op = (ModificationOperation)floatMod.Enum;
            var prop = (PropertyFloat)floatMod.Stat;
            var value = floatMod.Value;

            var sourceMod = GetSourceMod((RecipeSourceType)floatMod.Source, player, source);
            var targetMod = GetTargetMod((ModificationType)floatMod.Index, source, target, player, result);

            switch (op)
            {
                case ModificationOperation.SetValue:
                    player.UpdateProperty(targetMod, prop, value);
                    modified.Add(targetMod.Guid.Full);
                    if (Debug) Console.WriteLine($"{targetMod.Name}.SetProperty({prop}, {value}) - {op}");
                    break;
                case ModificationOperation.Add:
                    player.UpdateProperty(targetMod, prop, (targetMod.GetProperty(prop) ?? 0) + value);
                    modified.Add(targetMod.Guid.Full);
                    if (Debug) Console.WriteLine($"{targetMod.Name}.IncProperty({prop}, {value}) - {op}");
                    break;
                case ModificationOperation.CopyFromSourceToTarget:
                    player.UpdateProperty(target, prop, sourceMod.GetProperty(prop) ?? 0);
                    modified.Add(target.Guid.Full);
                    if (Debug) Console.WriteLine($"{target.Name}.SetProperty({prop}, {sourceMod.GetProperty(prop) ?? 0}) - {op}");
                    break;
                case ModificationOperation.CopyFromSourceToResult:
                    player.UpdateProperty(result, prop, player.GetProperty(prop) ?? 0);
                    modified.Add(result.Guid.Full);
                    if (Debug) Console.WriteLine($"{result.Name}.SetProperty({prop}, {player.GetProperty(prop) ?? 0}) - {op}");
                    break;
                default:
                    log.Warn($"RecipeManager.ModifyFloat({source.Name}, {target.Name}): unhandled operation {op}");
                    break;
            }
        }

        public static void ModifyString(Player player, RecipeModsString stringMod, WorldObject source, WorldObject target, WorldObject result, HashSet<uint> modified)
        {
            var op = (ModificationOperation)stringMod.Enum;
            var prop = (PropertyString)stringMod.Stat;
            var value = stringMod.Value;

            var sourceMod = GetSourceMod((RecipeSourceType)stringMod.Source, player, source);
            var targetMod = GetTargetMod((ModificationType)stringMod.Index, source, target, player, result);

            switch (op)
            {
                case ModificationOperation.SetValue:
                    player.UpdateProperty(targetMod, prop, value);
                    modified.Add(targetMod.Guid.Full);
                    if (Debug) Console.WriteLine($"{targetMod.Name}.SetProperty({prop}, {value}) - {op}");
                    break;
                case ModificationOperation.CopyFromSourceToTarget:
                    player.UpdateProperty(target, prop, sourceMod.GetProperty(prop) ?? sourceMod.Name);
                    modified.Add(target.Guid.Full);
                    if (Debug) Console.WriteLine($"{target.Name}.SetProperty({prop}, {sourceMod.GetProperty(prop) ?? sourceMod.Name}) - {op}");
                    break;
                case ModificationOperation.CopyFromSourceToResult:
                    player.UpdateProperty(result, prop, player.GetProperty(prop) ?? player.Name);
                    modified.Add(result.Guid.Full);
                    if (Debug) Console.WriteLine($"{result.Name}.SetProperty({prop}, {player.GetProperty(prop) ?? player.Name}) - {op}");
                    break;
                default:
                    log.Warn($"RecipeManager.ModifyString({source.Name}, {target.Name}): unhandled operation {op}");
                    break;
            }
        }

        public static void ModifyInstanceID(Player player, RecipeModsIID iidMod, WorldObject source, WorldObject target, WorldObject result, HashSet<uint> modified)
        {
            var op = (ModificationOperation)iidMod.Enum;
            var prop = (PropertyInstanceId)iidMod.Stat;
            var value = iidMod.Value;

            var sourceMod = GetSourceMod((RecipeSourceType)iidMod.Source, player, source);
            var targetMod = GetTargetMod((ModificationType)iidMod.Index, source, target, player, result);

            switch (op)
            {
                case ModificationOperation.SetValue:
                    player.UpdateProperty(targetMod, prop, value);
                    modified.Add(targetMod.Guid.Full);
                    if (Debug) Console.WriteLine($"{targetMod.Name}.SetProperty({prop}, {value}) - {op}");
                    break;
                case ModificationOperation.CopyFromSourceToTarget:
                    player.UpdateProperty(target, prop, ModifyInstanceIDRuleSet(prop, sourceMod, targetMod));
                    modified.Add(target.Guid.Full);
                    if (Debug) Console.WriteLine($"{target.Name}.SetProperty({prop}, {ModifyInstanceIDRuleSet(prop, sourceMod, targetMod)}) - {op}");
                    break;
                case ModificationOperation.CopyFromSourceToResult:
                    player.UpdateProperty(result, prop, ModifyInstanceIDRuleSet(prop, player, targetMod));     // ??
                    modified.Add(result.Guid.Full);
                    if (Debug) Console.WriteLine($"{result.Name}.SetProperty({prop}, {ModifyInstanceIDRuleSet(prop, player, targetMod)}) - {op}");
                    break;
                default:
                    log.Warn($"RecipeManager.ModifyInstanceID({source.Name}, {target.Name}): unhandled operation {op}");
                    break;
            }
        }

        private static uint ModifyInstanceIDRuleSet(PropertyInstanceId property, WorldObject sourceMod, WorldObject targetMod)
        {
            switch (property)
            {
                case PropertyInstanceId.AllowedWielder:
                case PropertyInstanceId.AllowedActivator:
                    return sourceMod.Guid.Full;
                default:
                    break;
            }

            return sourceMod.GetProperty(property) ?? 0;
        }

        public static void ModifyDataID(Player player, RecipeModsDID didMod, WorldObject source, WorldObject target, WorldObject result, HashSet<uint> modified)
        {
            var op = (ModificationOperation)didMod.Enum;
            var prop = (PropertyDataId)didMod.Stat;
            var value = didMod.Value;

            var sourceMod = GetSourceMod((RecipeSourceType)didMod.Source, player, source);
            var targetMod = GetTargetMod((ModificationType)didMod.Index, source, target, player, result);

            switch (op)
            {
                case ModificationOperation.SetValue:
                    player.UpdateProperty(targetMod, prop, value);
                    modified.Add(targetMod.Guid.Full);
                    if (Debug) Console.WriteLine($"{targetMod.Name}.SetProperty({prop}, {value}) - {op}");
                    break;
                case ModificationOperation.CopyFromSourceToTarget:
                    player.UpdateProperty(target, prop, sourceMod.GetProperty(prop) ?? 0);
                    modified.Add(target.Guid.Full);
                    if (Debug) Console.WriteLine($"{target.Name}.SetProperty({prop}, {sourceMod.GetProperty(prop) ?? 0}) - {op}");
                    break;
                case ModificationOperation.CopyFromSourceToResult:
                    player.UpdateProperty(result, prop, player.GetProperty(prop) ?? 0);
                    modified.Add(result.Guid.Full);
                    if (Debug) Console.WriteLine($"{result.Name}.SetProperty({prop}, {player.GetProperty(prop) ?? 0}) - {op}");
                    break;
                default:
                    log.Warn($"RecipeManager.ModifyDataID({source.Name}, {target.Name}): unhandled operation {op}");
                    break;
            }
        }

        private static void ModifyWeenieClassId(WorldObject target, uint weenieClassId, HashSet<uint> modified)
        {
            var newWeenie = DatabaseManager.World.GetCachedWeenie(weenieClassId);
            var oldWeenie = DatabaseManager.World.GetCachedWeenie(target.Biota.WeenieClassId);

            switch (target.ItemType)
            {
                case ItemType.MeleeWeapon:
                case ItemType.MissileWeapon:
                case ItemType.Caster:
                    ModifyWeenieWeapon(target, newWeenie);
                    break;
                default:
                    log.Error($"RecipeManager.ModifyWeenieClassId({target.Guid}, {weenieClassId}) Unsupported ItemType: {target.ItemType}");
                    return;
            }

            target.Biota.WeenieClassId = weenieClassId;
            ModifyWeenieName(target, oldWeenie, newWeenie);
            ModifyWeenieDescription(target, oldWeenie, newWeenie);

            modified.Add(target.Guid.Full);
        }

        private static void ModifyWeenieName(WorldObject target, Weenie oldWeenie, Weenie newWeenie)
        {
            var previousWeenieName = oldWeenie.GetProperty(PropertyString.Name);
            var newWeenieName = newWeenie.GetProperty(PropertyString.Name);

            var previousTargetName = target.GetProperty(PropertyString.Name);
            var newTargetName = previousTargetName.Replace(previousWeenieName, newWeenieName);

            target.SetProperty(PropertyString.Name, newTargetName);
        }

        private static void ModifyWeenieDescription(WorldObject target, Weenie oldWeenie, Weenie newWeenie)
        {
            var previousWeenieName = oldWeenie.GetProperty(PropertyString.Name);
            var newWeenieName = newWeenie.GetProperty(PropertyString.Name);

            var previousTargetDesc = target.GetProperty(PropertyString.LongDesc);
            var newTargetDesc = previousTargetDesc.Replace(previousWeenieName, newWeenieName);

            target.SetProperty(PropertyString.LongDesc, newTargetDesc);
        }

        private static void ModifyWeenieWeapon(WorldObject target, Weenie newWeenie)
        {
            var newDamageType = newWeenie.GetProperty(PropertyInt.DamageType);
            var newUiEffects = newWeenie.GetProperty(PropertyInt.UiEffects);
            var newSetup = newWeenie.GetProperty(PropertyDataId.Setup);

            if (newDamageType != null)
                target.SetProperty(PropertyInt.DamageType, (int) newDamageType);
            else
                target.RemoveProperty(PropertyInt.DamageType);

            if (newUiEffects != null)
                target.SetProperty(PropertyInt.UiEffects, (int) newUiEffects);
            else if (target.ProcSpell != null || target.Biota.HasKnownSpell(target.BiotaDatabaseLock))
                target.SetProperty(PropertyInt.UiEffects, (int) UiEffects.Magical);
            else
                target.RemoveProperty(PropertyInt.UiEffects);

            if (newSetup != null)
                target.SetProperty(PropertyDataId.Setup, (uint) newSetup);
            else
                target.RemoveProperty(PropertyDataId.Setup);
        }

        /// <summary>
        /// flag to use c# logic instead of mutate script logic
        /// </summary>
        private static readonly bool useMutateNative = true;

        public static bool TryMutate(Player player, WorldObject source, WorldObject target, Recipe recipe, uint dataId, HashSet<uint> modified)
        {
            if (useMutateNative)
                return TryMutateNative(player, source, target, recipe, dataId);

            switch (dataId)
            {
                case 0x38000042:
                    // Can this be done with mutation script?
                    switch (target.ItemHeritageGroupRestriction)
                    {
                        case "Aluvian":
                            target.HeritageGroup = HeritageGroup.Aluvian;
                            break;

                        case "Gharu'ndim":
                            target.HeritageGroup = HeritageGroup.Gharundim;
                            break;

                        case "Sho":
                            target.HeritageGroup = HeritageGroup.Sho;
                            break;

                        case "Viamontian":
                            target.HeritageGroup = HeritageGroup.Viamontian;
                            break;
                    }
                    break;
                case 0x38000051:
                case 0x38000052:
                case 0x38000053:
                case 0x38000054:
                case 0x38000055:
                case 0x38000056:
                case 0x38000057:
                    // If weapon is restance cleaving update cleaving element to match new weapon element.
                    if (target.ResistanceModifierType.HasValue && target.ResistanceModifierType != DamageType.Undef)
                        target.ResistanceModifierType = target.W_DamageType;
                    break;
            }            

            var numTimesTinkered = target.NumTimesTinkered;

            var mutationScript = MutationCache.GetMutation(dataId);

            if (mutationScript == null)
            {
                log.Error($"RecipeManager.TryApplyMutation({dataId:X8}, {target.Name}) - couldn't find mutation script");
                return false;
            }

            var result = mutationScript.TryMutate(target);

            if (numTimesTinkered != target.NumTimesTinkered)
                HandleTinkerLog(source, target);

            modified.Add(target.Guid.Full);

            return result;
        }

        private static void HandleTinkerLog(WorldObject source, WorldObject target)
        {
            if (target.TinkerLog != null)
                target.TinkerLog += ",";

            target.TinkerLog += (uint?)source.MaterialType ?? source.WeenieClassId;
        }

        public static uint MaterialDualDID = 0x27000000;

        public static string GetMaterialName(MaterialType materialType)
        {
            var dualDIDs = DatManager.PortalDat.ReadFromDat<DualDidMapper>(MaterialDualDID);

            if (!dualDIDs.ClientEnumToName.TryGetValue((uint)materialType, out var materialName))
            {
                log.Error($"RecipeManager.GetMaterialName({materialType}): couldn't find material name");
                return materialType.ToString();
            }
            return materialName.Replace("_", " ");
        }

        public static void AttemptCombineSalvageBags(Player player, WorldObject source, WorldObject target, bool confirmed = false)
        {
            // verify requirements
            if (!VerifyRequirements(null, player, source, target))
            {
                player.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
                return;
            }

            var showDialog = player.GetCharacterOption(CharacterOption.UseCraftingChanceOfSuccessDialog);

            var motionCommand = MotionCommand.ClapHands;

            var actionChain = new ActionChain();
            var nextUseTime = 0.0f;

            player.IsBusy = true;

            if (PropertyManager.GetBool("allow_combat_mode_crafting").Item && player.CombatMode != CombatMode.NonCombat)
            {
                // Drop out of combat mode.  This depends on the server property "allow_combat_mode_craft" being True.
                // If not, this action would have aborted due to not being in NonCombat mode.
                var stanceTime = player.SetCombatMode(CombatMode.NonCombat);
                actionChain.AddDelaySeconds(stanceTime);

                nextUseTime += stanceTime;
            }

            var motion = new Motion(player, motionCommand);
            var currentStance = player.CurrentMotionState.Stance; // expected to be MotionStance.NonCombat
            var clapTime = !confirmed ? Physics.Animation.MotionTable.GetAnimationLength(player.MotionTableId, currentStance, motionCommand) : 0.0f;

            if (!confirmed)
            {
                actionChain.AddAction(player, () => player.SendMotionAsCommands(motionCommand, currentStance));
                actionChain.AddDelaySeconds(clapTime);

                nextUseTime += clapTime;
            }

            if (showDialog && !confirmed)
            {
                actionChain.AddAction(player, () => ShowDialog(player, source, target, null, 1));
                actionChain.AddAction(player, () => player.IsBusy = false);
            }
            else
            {
                actionChain.AddAction(player, () => CombineSalvageBags(player, source, target));

                actionChain.AddAction(player, () =>
                {
                    if (!showDialog)
                        player.SendUseDoneEvent();

                    player.IsBusy = false;
                });
            }

            actionChain.EnqueueChain();

            player.NextUseTime = DateTime.UtcNow.AddSeconds(nextUseTime);
        }

        public static void CombineSalvageBags(Player player, WorldObject source, WorldObject target)
        {
            var amount = source.Structure.Value;
            var added = player.TryAddSalvage(target, source, amount);
            var remaining = amount - added;

            var valueFactor = (float)added / amount;
            var addedValue = (int)Math.Round((source.Value ?? 0) * valueFactor);
            target.Value = Math.Min((target.Value ?? 0) + addedValue, 75000);
            UpdateObj(player, target);

            if (remaining > 0)
            {
                source.Structure = (ushort)remaining;
                source.Value -= addedValue;
                source.Name = $"Salvage ({remaining})";
                UpdateObj(player, source);
            }
            else
                player.TryConsumeFromInventoryWithNetworking(source);
        }

        // todo: remove this once foolproof salvage recipes are updated
        private static readonly HashSet<WeenieClassName> foolproofTinkers = new HashSet<WeenieClassName>()
        {
            // rare foolproof
            WeenieClassName.W_MATERIALRAREFOOLPROOFAQUAMARINE_CLASS,
            WeenieClassName.W_MATERIALRAREFOOLPROOFBLACKGARNET_CLASS,
            WeenieClassName.W_MATERIALRAREFOOLPROOFBLACKOPAL_CLASS,
            WeenieClassName.W_MATERIALRAREFOOLPROOFEMERALD_CLASS,
            WeenieClassName.W_MATERIALRAREFOOLPROOFFIREOPAL_CLASS,
            WeenieClassName.W_MATERIALRAREFOOLPROOFIMPERIALTOPAZ_CLASS,
            WeenieClassName.W_MATERIALRAREFOOLPROOFJET_CLASS,
            WeenieClassName.W_MATERIALRAREFOOLPROOFPERIDOT_CLASS,
            WeenieClassName.W_MATERIALRAREFOOLPROOFREDGARNET_CLASS,
            WeenieClassName.W_MATERIALRAREFOOLPROOFSUNSTONE_CLASS,
            WeenieClassName.W_MATERIALRAREFOOLPROOFWHITESAPPHIRE_CLASS,
            WeenieClassName.W_MATERIALRAREFOOLPROOFYELLOWTOPAZ_CLASS,
            WeenieClassName.W_MATERIALRAREFOOLPROOFZIRCON_CLASS,

            // regular foolproof
            WeenieClassName.W_MATERIALACE36619FOOLPROOFAQUAMARINE,
            WeenieClassName.W_MATERIALACE36620FOOLPROOFBLACKGARNET,
            WeenieClassName.W_MATERIALACE36621FOOLPROOFBLACKOPAL,
            WeenieClassName.W_MATERIALACE36622FOOLPROOFEMERALD,
            WeenieClassName.W_MATERIALACE36623FOOLPROOFFIREOPAL,
            WeenieClassName.W_MATERIALACE36624FOOLPROOFIMPERIALTOPAZ,
            WeenieClassName.W_MATERIALACE36625FOOLPROOFJET,
            WeenieClassName.W_MATERIALACE36626FOOLPROOFREDGARNET,
            WeenieClassName.W_MATERIALACE36627FOOLPROOFSUNSTONE,
            WeenieClassName.W_MATERIALACE36628FOOLPROOFWHITESAPPHIRE,
            WeenieClassName.W_MATERIALACE36634FOOLPROOFPERIDOT,
            WeenieClassName.W_MATERIALACE36635FOOLPROOFYELLOWTOPAZ,
            WeenieClassName.W_MATERIALACE36636FOOLPROOFZIRCON,
        };
    }

    public static class RecipeExtensions
    {
        public static bool IsTinkering(this Recipe recipe)
        {
            return recipe.SalvageType > 0;
        }

        public static bool IsImbuing(this Recipe recipe)
        {
            return recipe.SalvageType == 2;
        }
    }
}
