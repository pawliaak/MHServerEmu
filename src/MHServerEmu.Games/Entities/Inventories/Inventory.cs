﻿using System.Collections;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.Entities.Inventories
{
    public class Inventory : IEnumerable<(uint, Inventory.InvEntry)>
    {
        public const uint InvalidSlot = uint.MaxValue;      // 0xFFFFFFFF / -1

        private static readonly Logger Logger = LogManager.CreateLogger();

        private HashSet<(uint, InvEntry)> _entities = new();

        public Game Game { get; }
        public ulong OwnerId { get; private set; }
        public Entity Owner { get => Game.EntityManager.GetEntity<Entity>(OwnerId); }

        public InventoryPrototype Prototype { get; private set; }
        public PrototypeId PrototypeDataRef { get => Prototype != null ? Prototype.DataRef : PrototypeId.Invalid; }

        public InventoryCategory Category { get; private set; } = InventoryCategory.None;
        public InventoryConvenienceLabel ConvenienceLabel { get; private set; } = InventoryConvenienceLabel.None;
        public int MaxCapacity { get; private set; }

        public int Count { get => _entities.Count; }

        public Inventory(Game game)
        {
            Game = game;
        }

        public bool Initialize(PrototypeId prototypeRef, ulong ownerId)
        {
            var prototype = prototypeRef.As<InventoryPrototype>();
            if (prototype == null) return Logger.WarnReturn(false, "Initialize(): prototype == null");

            Prototype = prototype;
            OwnerId = ownerId;
            Category = prototype.Category;
            ConvenienceLabel = prototype.ConvenienceLabel;
            MaxCapacity = prototype.CapacityUnlimited ? int.MaxValue : prototype.Capacity;

            if (ownerId == Entity.InvalidId) return Logger.WarnReturn(false, "Initialize(): ownerId == Entity.InvalidId");
            return true;
        }

        public int GetCapacity()
        {
            return 0;
        }

        public bool IsSlotFree(uint slot)
        {
            return false;
        }

        public static InventoryResult ChangeEntityInventoryLocation(Entity entity, Inventory destination, uint slot, ref ulong stackEntityId, bool useStacking)
        {
            InventoryLocation invLoc = entity.InventoryLocation;

            if (destination != null)
            {
                // If we have a valid destination, it means we are either adding this entity for the first time,
                // or it is already present in the destination inventory, and we are moving it to another slot.
                
                if (invLoc.IsValid == false)
                    return destination.AddEntity(entity, ref stackEntityId, useStacking, slot, InventoryLocation.Invalid);

                Inventory prevInventory = entity.GetOwnerInventory();

                if (prevInventory == null)
                    return Logger.WarnReturn(InventoryResult.NotInInventory,
                        $"ChangeEntityInventoryLocation(): Unable to get owner inventory for move with entity {entity.Id} at invLoc {invLoc}");

                return prevInventory.MoveEntityTo(entity, destination, ref stackEntityId, useStacking, slot);
            }
            else
            {
                // If no valid destination is specified, it means we are removing an entity from the inventory it is currently in

                if (invLoc.IsValid == false)
                    return Logger.WarnReturn(InventoryResult.NotInInventory,
                        $"ChangeEntityInventoryLocation(): Trying to remove entity {entity.Id} from inventory, but it is not in any inventory");

                Inventory inventory = entity.GetOwnerInventory();

                if (inventory == null)
                    return Logger.WarnReturn(InventoryResult.NotInInventory,
                        $"ChangeEntityInventoryLocation(): Unable to get owner inventory for remove with entity {entity.Id} at invLoc {invLoc}");

                return inventory.RemoveEntity(entity);
            }
        }

        public static bool IsPlayerStashInventory(PrototypeId inventoryRef)
        {
            if (inventoryRef == PrototypeId.Invalid)
                return Logger.WarnReturn(false, "IsPlayerStashInventory(): inventoryRef == PrototypeId.Invalid");

            var inventoryProto = GameDatabase.GetPrototype<InventoryPrototype>(inventoryRef);
            if (inventoryProto == null)
                return Logger.WarnReturn(false, "IsPlayerStashInventory(): inventoryProto == null");

            return inventoryProto.IsPlayerStashInventory();
        }

        private InventoryResult AddEntity(Entity entity, ref ulong stackEntityId, bool useStacking, uint slot, InventoryLocation prevInvLoc)
        {
            // NOTE: The entity is actually added at the very end in DoAddEntity(). Everything before it is validation.

            if (entity == null) return Logger.WarnReturn(InventoryResult.InvalidSourceEntity, "AddEntity(): entity == null");
            if (entity.IsRootOwner == false) return Logger.WarnReturn(InventoryResult.NotRootOwner, "AddEntity(): entity.IsRootOwner == false");

            // Look for a free slot of no slot if specified
            if (slot == InvalidSlot)
                slot = GetFreeSlot(entity, useStacking, true);

            // If we still don't have a slot, it means we have nowhere to put our item
            if (slot == InvalidSlot)
                return InventoryResult.InventoryFull;

            ulong existingEntityId = GetEntityInSlot(slot);
            if (existingEntityId != Entity.InvalidId)
            {
                var existingEntity = Game.EntityManager.GetEntity<Entity>(existingEntityId);
                if (existingEntity == null) return Logger.WarnReturn(InventoryResult.InvalidExistingEntityAtDest, "AddEntity(): existingEntity == null");

                if (useStacking && entity.CanStackOnto(existingEntity))
                    return DoStacking(entity, existingEntity, ref stackEntityId);
            }

            InventoryResult result = CheckAddEntity(entity, slot);
            if (result != InventoryResult.Success) return result;

            return DoAddEntity(entity, slot, prevInvLoc);
        }

        private InventoryResult DoAddEntity(Entity entity, uint slot, InventoryLocation prevInvLoc)
        {
            if (entity == null) return Logger.WarnReturn(InventoryResult.InvalidSourceEntity, "DoAddEntity(): entity == null");

            Entity owner = Game.EntityManager.GetEntity<Entity>(OwnerId);
            if (owner == null) return Logger.WarnReturn(InventoryResult.InventoryHasNoOwner, "DoAddEntity(): owner == null");

            if (slot < 0) return Logger.WarnReturn(InventoryResult.InvalidSlotParam, "DoAddEntity(): slot < 0");    // This is pretty meaningless with uint, but let's just follow the client
            if (slot >= GetCapacity()) return Logger.WarnReturn(InventoryResult.SlotExceedsCapacity, "DoAddEntity(): slot >= GetCapacity()");

            if (GetEntityInSlot(slot) != Entity.InvalidId) return InventoryResult.SlotAlreadyOccupied;

            InventoryLocation existingInvLoc = entity.InventoryLocation;

            if (existingInvLoc.IsValid)
                return Logger.WarnReturn(InventoryResult.SourceEntityAlreadyInAnInventory,
                    $"DoAddEntity(): Entity {entity.Id} not expected in inventory, but is located at {existingInvLoc}");

            PreAdd(entity);

            _entities.Add((slot, new InvEntry(entity.Id, entity.PrototypeDataRef, null)));
            entity.InventoryLocation.Set(OwnerId, PrototypeDataRef, slot);
            InventoryLocation invLoc = entity.InventoryLocation;

            PostAdd(entity, prevInvLoc, invLoc);
            PostFinalMove(entity, prevInvLoc, invLoc);
            owner.OnOtherEntityAddedToMyInventory(entity, invLoc, false);

            return InventoryResult.Success;
        }

        private InventoryResult MoveEntityTo(Entity entity, Inventory destination, ref ulong stackEntityId, bool useStacking, uint slot)
        {
            return InventoryResult.Invalid;
        }

        private InventoryResult RemoveEntity(Entity entity)
        {
            return InventoryResult.Invalid;
        }

        private InventoryResult DoStacking(Entity source, Entity destination, ref ulong stackEntityId)
        {
            return InventoryResult.Invalid;
        }

        private InventoryResult CheckAddEntity(Entity entity, uint slot)
        {
            if (entity == null) return Logger.WarnReturn(InventoryResult.InvalidSourceEntity, "CheckAddEntity(): entity == null");
            if (slot == InvalidSlot) return Logger.WarnReturn(InventoryResult.InvalidSlotParam, "CheckAddEntity(): slot == InvalidSlot");

            InventoryResult canChangeInvResult = entity.CanChangeInventoryLocation(this);
            if (canChangeInvResult != InventoryResult.Success)
                return Logger.WarnReturn(canChangeInvResult, "CheckAddEntity(): canChangeInvResult != InventoryResult.Success");

            if (IsSlotFree(slot) == false) return InventoryResult.SlotAlreadyOccupied;

            return InventoryResult.Success;
        }

        private uint GetFreeSlot(Entity entity, bool useStacking, bool isAdding = false)
        {
            return InvalidSlot;
        }

        private ulong GetEntityInSlot(uint slot)
        {
            foreach (var entry in this)
            {
                if (entry.Item1 == slot)
                    return entry.Item2.EntityId;
            }

            return Entity.InvalidId;
        }

        private void PreAdd(Entity entity)
        {

        }

        private void PostAdd(Entity entity, InventoryLocation prevInvLoc, InventoryLocation invLoc)
        {

        }

        private void PreRemove(Entity entity)
        {

        }

        private void PostRemove(Entity entity, InventoryLocation prevInvLoc, bool unkBool)
        {

        }

        private void PostFinalMove(Entity entity, InventoryLocation prevInvLoc, InventoryLocation invLoc)
        {

        }

        // Inventory::Iterator
        public IEnumerator<(uint, InvEntry)> GetEnumerator() => _entities.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public readonly struct InvEntry : IComparable<InvEntry>
        {
            public ulong EntityId { get; }
            public PrototypeId PrototypeDataRef { get; }
            public InventoryMetaData MetaData { get; }

            public InvEntry()
            {
                EntityId = 0;
                PrototypeDataRef = PrototypeId.Invalid;
                MetaData = null;
            }

            public InvEntry(ulong entityId, PrototypeId prototypeDataRef, InventoryMetaData metaData)
            {
                EntityId = entityId;
                PrototypeDataRef = prototypeDataRef;
                MetaData = metaData;
            }

            public InvEntry(InvEntry other)
            {
                EntityId = other.EntityId;
                PrototypeDataRef = other.PrototypeDataRef;
                MetaData = other.MetaData;
            }

            public int CompareTo(InvEntry other) => EntityId.CompareTo(other.EntityId);
        }
    }
}
