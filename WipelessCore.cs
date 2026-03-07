using System;
using System.Collections;
using System.Collections.Generic;
using Oxide.Core;
using UnityEngine;
using System.Linq;
using System.Reflection;

namespace Oxide.Plugins
{
    [Info("WipelessCore", "Vidmoris", "1.0.0")]
    public class WipelessCore : RustPlugin
    {
        private StoredData storedData;
        private bool isSystemLoading = false;
        private bool preLoadStabilityState = true;
        private Timer autoSaveTimer;
        
        private readonly FieldInfo isDataLoadedField = typeof(BaseEntity).GetField("isDataLoaded", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private const string AdminPermission = "wipelesscore.admin";

        #region Configuration
        private float AutoSaveInterval = 30f; // Minutes

        protected override void LoadDefaultConfig()
        {
            Config["AutoSaveIntervalMinutes"] = 30.0;
            SaveConfig();
        }

        private void Init()
        {
            // Register the security permission for your commands
            permission.RegisterPermission(AdminPermission, this);

            AutoSaveInterval = Convert.ToSingle(Config["AutoSaveIntervalMinutes"] ?? 30.0);
            
            autoSaveTimer = timer.Every(AutoSaveInterval * 60f, () => 
            {
                Puts("Auto-saving world state for 'Forever' persistence...");
                ExecuteSave(null); 
            });
        }
        #endregion

        // Helper method to check if a player is allowed to use commands
        private bool HasAccess(BasePlayer player)
        {
            if (player == null) return true; // Console always has access
            return permission.UserHasPermission(player.UserIDString, AdminPermission);
        }

        public class ItemData {
            public int ItemID; public int Amount; public ulong SkinID; public int Slot; public float Condition; public float MaxCondition;
        }

        public class PointData { public float x, y, z; }

        public class WireData {
            public int SourceSlot; public int TargetID; public int TargetSlot;
            public int WireColor; public int WireType;
            public List<PointData> LinePoints;
        }

        public class SellOrderData {
            public int ItemID; public int Amount; public int CurrencyID; public int CurrencyAmount; public bool ItemIsBP; public bool CurrencyIsBP;
        }

        public class EntityData {
            public int ID; public int ParentID; public string PrefabName;
            public float x, y, z, rx, ry, rz, rw;
            public BuildingGrade.Enum Grade; public int Priority; public float Health;
            public ulong OwnerID; public ulong DeployerID; public uint BuildingID;
            public bool IsOpen; public ulong SkinID; 
            public bool IsLocked; public string LockCode; public string GuestCode;
            public string BagName; public List<ulong> TurretAuth;
            public List<SellOrderData> VendingOrders; public List<ulong> LockWhitelist;
            public List<ulong> GuestWhitelist; public List<ulong> TCAuthList;
            public List<ItemData> Inventory; public List<WireData> Connections;
            public string IOString; public float IOFloat; public int IOInt;
            public float BatteryCharge; public List<int> ConveyorFilters;
        }
        
        public class StoredData { 
            public List<EntityData> Entities = new List<EntityData>(); 
        }

        void Loaded() => storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name) ?? new StoredData();

        void Unload() {
            autoSaveTimer?.Destroy();
            if (isSystemLoading) ConVar.Server.stability = preLoadStabilityState;
        }

        int GetPriority(string prefab) {
            if (string.IsNullOrEmpty(prefab)) return 7;
            prefab = prefab.ToLower();
            if (prefab.Contains("foundation")) return 0;
            if (prefab.Contains("wall") || prefab.Contains("pillar")) return 1;
            if (prefab.Contains("floor") || prefab.Contains("roof") || prefab.Contains("stairs")) return 2;
            if (prefab.Contains("door") || prefab.Contains("hatch")) return 3;
            if (prefab.Contains("box") || prefab.Contains("cupboard") || prefab.Contains("stash") || prefab.Contains("oven") || prefab.Contains("furnace") || prefab.Contains("vending")) return 4;
            if (prefab.Contains("lock")) return 6; 
            return 5; 
        }

        bool IsValidTarget(BaseEntity e) {
            if (e == null || e.IsDestroyed || e.net == null) return false;
            if (e.OwnerID == 0) return false; 
            if (e is BasePlayer || e is BaseNpc || e is BaseCorpse || e is LootContainer || e is DroppedItem || e is DroppedItemContainer) return false;
            if (e.HasParent() && (e.GetParentEntity() is BaseVehicle)) return false;
            return e is BuildingBlock || e is Door || e is BaseLock || e is StorageContainer || e is IOEntity || e is DecayEntity || e is BaseCombatEntity;
        }

        object GetValue(object obj, string name) {
            if (obj == null) return null;
            var type = obj.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return field.GetValue(obj);
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return prop?.GetValue(obj, null);
        }

        void SetValue(object obj, string name, object val) {
            if (obj == null) return;
            var type = obj.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) { try { field.SetValue(obj, Convert.ChangeType(val, field.FieldType)); } catch { } return; }
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null) { try { prop.SetValue(obj, Convert.ChangeType(val, prop.PropertyType), null); } catch { } }
        }

        [ChatCommand("save")]
        void cmdSave(BasePlayer player) {
            if (!HasAccess(player)) return;
            ExecuteSave(player);
        }

        private void ExecuteSave(BasePlayer requester) {
            var allEntities = BaseNetworkable.serverEntities.OfType<BaseEntity>().Where(IsValidTarget).ToList();
            Dictionary<BaseEntity, int> entToId = new Dictionary<BaseEntity, int>();
            int currentId = 1;

            foreach (var ent in allEntities) entToId[ent] = currentId++;

            storedData.Entities.Clear();

            foreach (var ent in allEntities) {
                var eData = new EntityData {
                    ID = entToId[ent], PrefabName = ent.PrefabName,
                    x = ent.transform.position.x, y = ent.transform.position.y, z = ent.transform.position.z,
                    rx = ent.transform.rotation.x, ry = ent.transform.rotation.y, rz = ent.transform.rotation.z, rw = ent.transform.rotation.w,
                    Priority = GetPriority(ent.PrefabName), OwnerID = ent.OwnerID, SkinID = ent.skinID, 
                    IsLocked = ent.HasFlag(BaseEntity.Flags.Locked) 
                };

                if (ent.HasParent() && entToId.ContainsKey(ent.GetParentEntity())) eData.ParentID = entToId[ent.GetParentEntity()];
                if (ent is DecayEntity decayEnt) eData.BuildingID = decayEnt.buildingID;
                if (ent is BaseCombatEntity combatEnt) eData.Health = combatEnt.health;
                if (ent is BuildingBlock bb) eData.Grade = bb.grade;
                if (ent is Door door) eData.IsOpen = door.IsOpen();
                
                if (ent is StorageContainer container && container.inventory != null) {
                    eData.Inventory = container.inventory.itemList.Select(item => new ItemData {
                        ItemID = item.info.itemid, Amount = item.amount, SkinID = item.skin, Slot = item.position, Condition = item.condition, MaxCondition = item.maxCondition
                    }).ToList();
                    if (ent is BuildingPrivlidge tc) eData.TCAuthList = tc.authorizedPlayers.ToList();
                }

                if (ent is CodeLock cLock) {
                    eData.LockCode = cLock.code;
                    if (cLock.whitelistPlayers != null) eData.LockWhitelist = cLock.whitelistPlayers.ToList();
                    eData.GuestCode = cLock.guestCode;
                    if (cLock.guestPlayers != null) eData.GuestWhitelist = cLock.guestPlayers.ToList();
                }

                if (ent is SleepingBag bag) { eData.BagName = bag.niceName; eData.DeployerID = bag.deployerUserID; }
                if (ent is AutoTurret turret && turret.authorizedPlayers != null) eData.TurretAuth = turret.authorizedPlayers.ToList();

                if (ent is VendingMachine vm && vm.sellOrders?.sellOrders != null) {
                    eData.VendingOrders = vm.sellOrders.sellOrders.Select(o => new SellOrderData {
                        ItemID = Convert.ToInt32(GetValue(o, "itemToSellID") ?? 0),
                        Amount = Convert.ToInt32(GetValue(o, "itemToSellAmount") ?? 0),
                        CurrencyID = Convert.ToInt32(GetValue(o, "currencyID") ?? 0),
                        CurrencyAmount = Convert.ToInt32(GetValue(o, "currencyAmountPerItem") ?? 0),
                        ItemIsBP = Convert.ToBoolean(GetValue(o, "itemIsBlueprint") ?? false),
                        CurrencyIsBP = Convert.ToBoolean(GetValue(o, "currencyIsBlueprint") ?? false)
                    }).ToList();
                }

                if (ent is IOEntity ioEnt) {
                    eData.IOString = GetValue(ioEnt, "rcIdentifier") as string;
                    if (GetValue(ioEnt, "timerLength") is float fTimer) eData.IOFloat = fTimer;
                    var ioVal = GetValue(ioEnt, "branchAmount") ?? GetValue(ioEnt, "frequency");
                    if (ioVal != null) eData.IOInt = Convert.ToInt32(ioVal);
                    var charge = GetValue(ioEnt, "charge") ?? GetValue(ioEnt, "capacity");
                    if (charge is float fCharge) eData.BatteryCharge = fCharge;

                    if (ioEnt is IndustrialConveyor conveyor && conveyor.filterItems != null) {
                        eData.ConveyorFilters = conveyor.filterItems.Select(f => (GetValue(f, "targetItem") as ItemDefinition)?.itemid ?? 0).Where(id => id != 0).ToList();
                    }

                    if (ioEnt.outputs != null) {
                        eData.Connections = new List<WireData>();
                        for (int i = 0; i < ioEnt.outputs.Length; i++) {
                            var output = ioEnt.outputs[i];
                            var targetEnt = output.connectedTo.Get(true);
                            if (targetEnt != null && entToId.ContainsKey(targetEnt)) {
                                eData.Connections.Add(new WireData {
                                    SourceSlot = i, TargetID = entToId[targetEnt], TargetSlot = output.connectedToSlot,
                                    WireColor = (int)output.wireColour, WireType = (int)output.type,
                                    LinePoints = output.linePoints?.Select(p => new PointData { x = p.x, y = p.y, z = p.z }).ToList() ?? new List<PointData>()
                                });
                            }
                        }
                    }
                }
                storedData.Entities.Add(eData);
            }

            Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);
            
            // CUSTOM LOG: Output the saved amount to console
            Puts($"saved ammount of assets: {storedData.Entities.Count}");
            if (requester != null) PrintToChat(requester, $"Forever-Save Complete. {storedData.Entities.Count} entities tracked.");
        }

        [ChatCommand("load")]
        void cmdLoad(BasePlayer player) {
            if (!HasAccess(player)) return;
            if (isSystemLoading) { PrintToChat(player, "A load is already in progress."); return; }
            ServerMgr.Instance.StartCoroutine(StrictLoadRoutine(player));
        }

        IEnumerator StrictLoadRoutine(BasePlayer player) {
            isSystemLoading = true;
            preLoadStabilityState = ConVar.Server.stability;
            ConVar.Server.stability = false;

            var sorted = storedData.Entities.OrderBy(e => e.Priority).ToList();
            List<BaseEntity> spawnedEntities = new List<BaseEntity>();
            Dictionary<uint, uint> idMap = new Dictionary<uint, uint>();
            Dictionary<int, BaseEntity> idToEnt = new Dictionary<int, BaseEntity>();

            foreach (var data in sorted) {
                BaseEntity ent = GameManager.server.CreateEntity(data.PrefabName, new Vector3(data.x, data.y, data.z), new Quaternion(data.rx, data.ry, data.rz, data.rw));
                if (ent != null) {
                    idToEnt[data.ID] = ent;
                    
                    ent.OwnerID = data.OwnerID;
                    ent.skinID = data.SkinID; 

                    if (!(ent is SleepingBag) && isDataLoadedField != null) isDataLoadedField.SetValue(ent, true);
                    
                    if (ent is BuildingBlock bb) {
                        bb.grade = data.Grade;
                        bb.skinID = data.SkinID;
                        bb.UpdateSkin();
                    }
                    
                    if (ent is DecayEntity decayEnt && data.BuildingID != 0) {
                        if (!idMap.ContainsKey(data.BuildingID)) idMap[data.BuildingID] = BuildingManager.server.NewBuildingID();
                        decayEnt.buildingID = idMap[data.BuildingID];
                    }

                    if (ent is SleepingBag bag) {
                        bag.niceName = string.IsNullOrEmpty(data.BagName) ? "Sleeping Bag" : data.BagName;
                        bag.deployerUserID = data.DeployerID != 0 ? data.DeployerID : data.OwnerID;
                    }

                    if (ent is CodeLock cLock) {
                        if (!string.IsNullOrEmpty(data.LockCode)) { cLock.code = data.LockCode; cLock.hasCode = true; }
                        if (data.LockWhitelist != null) { cLock.whitelistPlayers.Clear(); foreach (var p in data.LockWhitelist) cLock.whitelistPlayers.Add(p); }
                        if (!string.IsNullOrEmpty(data.GuestCode)) { cLock.guestCode = data.GuestCode; cLock.hasGuestCode = true; }
                        if (data.GuestWhitelist != null) { cLock.guestPlayers.Clear(); foreach (var p in data.GuestWhitelist) cLock.guestPlayers.Add(p); }
                    }

                    if (ent is BaseLock && data.IsLocked) ent.SetFlag(BaseEntity.Flags.Locked, true);

                    if (ent is BaseLock && data.ParentID != 0 && idToEnt.ContainsKey(data.ParentID)) {
                        var parent = idToEnt[data.ParentID];
                        ent.SetParent(parent, parent.GetSlotAnchorName(BaseEntity.Slot.Lock));
                        ent.transform.localPosition = Vector3.zero;
                        ent.transform.localRotation = Quaternion.identity;
                        ent.Spawn();
                        parent.SetSlot(BaseEntity.Slot.Lock, ent);
                    } else {
                        if (data.ParentID != 0 && idToEnt.ContainsKey(data.ParentID)) ent.SetParent(idToEnt[data.ParentID], true);
                        ent.Spawn();
                    }

                    if (ent is SleepingBag bagPost) {
                        bagPost.unlockTime = 0f; 
                    }

                    if (ent is BuildingBlock bbPost) bbPost.SetHealth(data.Health);
                    else if (ent is Door door) door.SetOpen(data.IsOpen);
                    else if (ent is StorageContainer container) {
                        if (data.Inventory != null) {
                            container.inventory.Clear();
                            foreach (var i in data.Inventory) {
                                Item item = ItemManager.CreateByItemID(i.ItemID, i.Amount, i.SkinID);
                                if (item != null) item.MoveToContainer(container.inventory, i.Slot);
                            }
                        }
                        if (container is BuildingPrivlidge tc && data.TCAuthList != null) tc.authorizedPlayers = new HashSet<ulong>(data.TCAuthList);
                    }

                    if (ent is AutoTurret turretPost && data.TurretAuth != null) turretPost.authorizedPlayers = new HashSet<ulong>(data.TurretAuth);

                    if (ent is VendingMachine vmPost && data.VendingOrders != null) {
                        vmPost.sellOrders = new ProtoBuf.VendingMachine.SellOrderContainer { sellOrders = new List<ProtoBuf.VendingMachine.SellOrder>() };
                        foreach (var o in data.VendingOrders) {
                            var n = new ProtoBuf.VendingMachine.SellOrder();
                            SetValue(n, "itemToSellID", o.ItemID); SetValue(n, "itemToSellAmount", o.Amount);
                            SetValue(n, "currencyID", o.CurrencyID); SetValue(n, "currencyAmountPerItem", o.CurrencyAmount);
                            SetValue(n, "itemIsBlueprint", o.ItemIsBP); SetValue(n, "currencyIsBlueprint", o.CurrencyIsBP);
                            vmPost.sellOrders.sellOrders.Add(n);
                        }
                    }

                    if (ent is IOEntity ioEnt) {
                        if (!string.IsNullOrEmpty(data.IOString)) SetValue(ioEnt, "rcIdentifier", data.IOString);
                        if (data.IOFloat > 0) SetValue(ioEnt, "timerLength", data.IOFloat);
                        if (data.IOInt > 0) { SetValue(ioEnt, "branchAmount", data.IOInt); SetValue(ioEnt, "frequency", data.IOInt); }
                        if (data.BatteryCharge > 0) { SetValue(ioEnt, "charge", data.BatteryCharge); SetValue(ioEnt, "capacity", data.BatteryCharge); }
                        if (ioEnt is IndustrialConveyor conv && data.ConveyorFilters != null) {
                            conv.filterItems = new List<IndustrialConveyor.ItemFilter>();
                            foreach (var id in data.ConveyorFilters) {
                                var def = ItemManager.FindItemDefinition(id);
                                if (def != null) {
                                    var f = new IndustrialConveyor.ItemFilter();
                                    object b = f; SetValue(b, "targetItem", def); conv.filterItems.Add((IndustrialConveyor.ItemFilter)b);
                                }
                            }
                        }
                    }
                    spawnedEntities.Add(ent);
                }
                yield return new WaitForSeconds(0.01f);
            }

            foreach (var data in sorted) {
                if (data.Connections != null && idToEnt.ContainsKey(data.ID) && idToEnt[data.ID] is IOEntity sIO) {
                    foreach (var c in data.Connections) {
                        if (idToEnt.ContainsKey(c.TargetID) && idToEnt[c.TargetID] is IOEntity tIO) {
                            if (c.SourceSlot < sIO.outputs.Length && c.TargetSlot < tIO.inputs.Length) {
                                var o = sIO.outputs[c.SourceSlot]; var i = tIO.inputs[c.TargetSlot];
                                o.connectedTo.Set(tIO); o.connectedToSlot = c.TargetSlot; o.wireColour = (WireTool.WireColour)c.WireColor; o.type = (IOEntity.IOType)c.WireType;
                                if (c.LinePoints != null) o.linePoints = c.LinePoints.Select(p => new Vector3(p.x, p.y, p.z)).ToArray();
                                i.connectedTo.Set(sIO); i.connectedToSlot = c.SourceSlot; i.wireColour = (WireTool.WireColour)c.WireColor; i.type = (IOEntity.IOType)c.WireType;
                                tIO.MarkDirty(); tIO.SendNetworkUpdate();
                            }
                        }
                    }
                    sIO.MarkDirty(); sIO.SendNetworkUpdate();
                }
            }

            foreach (var ent in spawnedEntities) {
                if (ent == null) continue;
                if (!(ent is SleepingBag) && isDataLoadedField != null) isDataLoadedField.SetValue(ent, false);
                
                if (ent is BuildingBlock bb) bb.UpdateSkin(); 
                if (ent is DecayEntity de) BuildingManager.server.Add(de);
                ent.SendNetworkUpdate();
            }

            ConVar.Server.stability = preLoadStabilityState;
            isSystemLoading = false;

            // CUSTOM LOG: Output the loaded amount to console
            Puts($"Loaded ammount of assets: {spawnedEntities.Count}");
            
            timer.Once(3f, () => {
                foreach (var activePlayer in BasePlayer.activePlayerList) {
                    if (activePlayer != null && activePlayer.IsConnected) {
                        activePlayer.SendRespawnOptions();
                    }
                }
                if (player != null) PrintToChat(player, "Forever-Load Map UI synchronized.");
            });

            if (player != null) PrintToChat(player, "Forever-Load Complete.");
        }

        [ChatCommand("wipe")]
        void cmdWipe(BasePlayer player) {
            if (!HasAccess(player)) return;
            
            var toDestroy = BaseNetworkable.serverEntities.OfType<BaseEntity>().Where(IsValidTarget).ToList();
            int destroyedCount = 0;
            foreach (var ent in toDestroy) {
                if (ent != null && !ent.IsDestroyed) { ent.Kill(BaseNetworkable.DestroyMode.None); destroyedCount++; }
            }
            
            // CUSTOM LOG: Output the wiped amount to console
            Puts($"wiped ammount of assets: {destroyedCount}");
            if (player != null) PrintToChat(player, $"Wipe Complete. {destroyedCount} entities removed.");
        }
    }
}
