using System;
using System.Collections.Generic;
using System.Linq;
using NitroxClient.Communication.Abstract;
using NitroxClient.GameLogic.Helper;
using NitroxClient.GameLogic.Spawning.Metadata;
using NitroxClient.MonoBehaviours;
using NitroxClient.Unity.Helper;
using NitroxModel.DataStructures;
using NitroxModel.DataStructures.GameLogic;
using NitroxModel.DataStructures.GameLogic.Entities;
using NitroxModel.DataStructures.GameLogic.Entities.Metadata;
using NitroxModel.DataStructures.Util;
using NitroxModel.Packets;
using NitroxModel_Subnautica.DataStructures;
using UnityEngine;

namespace NitroxClient.GameLogic;

public class Items
{
    private readonly IPacketSender packetSender;
    private readonly Entities entities;
    public static GameObject PickingUpObject { get; private set; }
    private readonly EntityMetadataManager entityMetadataManager;

    /// <summary>
    /// Whether or not <see cref="Inventory.Pickup"/> is running. It's useful to discriminate between Inventory.Pickup from
    /// a regular <see cref="Pickupable.Pickup"/>
    /// </summary>
    public bool IsInventoryPickingUp;

    public Items(IPacketSender packetSender, Entities entities, EntityMetadataManager entityMetadataManager)
    {
        this.packetSender = packetSender;
        this.entities = entities;
        this.entityMetadataManager = entityMetadataManager;
    }

    public void PickedUpByPlayer(GameObject gameObject, TechType techType)
    {
        // Newly created objects are normally placed into the player's inventory.
        // PickedUp was designed for this, but also works when an item is being moved into e.g. a vehicle's storage.
        // Consider making a new packet type if PickedUp is found to be impractical for this case.

        if (!Player.main.TryGetNitroxId(out NitroxId playerId))
        {
            Log.ErrorOnce($"[{nameof(Items)}] Player has no id! Could not set parent of picked up item {gameObject.name}.");
            return;
        }

        PickedUp(gameObject, techType, playerId);
    }

    public void PickedUp(GameObject gameObject, TechType techType, NitroxId containerId)
    {
        PickingUpObject = gameObject;

        // Try catch to avoid blocking PickingUpObject with a non null value outside of the current context
        try
        {
            InventoryItemEntity inventoryItemEntity = ConvertToInventoryEntityUntracked(gameObject, containerId);

            if (inventoryItemEntity.TechType.ToUnity() != techType)
            {
                Log.Warn($"Provided TechType: {techType} is different than the one automatically attributed to the item {inventoryItemEntity.TechType}");
            }

            PickupItem pickupItem = new(inventoryItemEntity);

            if (packetSender.Send(pickupItem))
            {
                Log.Debug($"Picked up item {inventoryItemEntity}");
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception);
        }
        PickingUpObject = null;
    }

    public void Planted(GameObject gameObject, NitroxId parentId)
    {
        InventoryItemEntity inventoryItemEntity = ConvertToInventoryEntityUntracked(gameObject, parentId);

        if (packetSender.Send(new EntitySpawnedByClient(inventoryItemEntity, true)))
        {
            Log.Debug($"Planted item {inventoryItemEntity}");
        }
    }

    /// <summary>
    /// Tracks the object (as dropped) and notifies the server to spawn the item for other players.
    /// </summary>
    public void Dropped(GameObject gameObject, TechType? techType = null)
    {
        techType ??= CraftData.GetTechType(gameObject);
        // there is a theoretical possibility of a stray remote tracking packet that re-adds the monobehavior, this is purely a safety call.
        RemoveAnyRemoteControl(gameObject);

        // WaterParkCreatures need at least one ManagedUpdate to run so their data is correctly refreshed (isMature and timeNextBreed)
        if (gameObject.TryGetComponent(out WaterParkCreature waterParkCreature))
        {
            waterParkCreature.ManagedUpdate();
        }

        NitroxId id = NitroxEntity.GetIdOrGenerateNew(gameObject);
        Optional<EntityMetadata> metadata = entityMetadataManager.Extract(gameObject);
        string classId = gameObject.GetComponent<PrefabIdentifier>().ClassId;

        WorldEntity droppedItem;
        List<Entity> childrenEntities = GetPrefabChildren(gameObject, id, entityMetadataManager).ToList();

        // If the item is dropped in a WaterPark we need to handle it differently
        NitroxId parentId = null;
        if (IsGlobalRootObject(gameObject) || (gameObject.GetComponent<Pickupable>() && TryGetParentWaterParkId(gameObject.transform.parent, out parentId)))
        {
            // We cast it to an entity type that is always seeable by clients
            // therefore, the packet will be redirected to everyone
            droppedItem = new GlobalRootEntity(gameObject.transform.ToLocalDto(), 0, classId, true, id, techType.Value.ToDto(), metadata.OrNull(), parentId, childrenEntities);
        }
        else if (gameObject.TryGetComponent(out OxygenPipe oxygenPipe))
        {
            // We can't spawn an OxygenPipe without its parent and root
            // Dropped patch is called in OxygenPipe.PlaceInWorld which is why OxygenPipe.ghostModel is valid
            IPipeConnection parentConnection = OxygenPipe.ghostModel.GetParent();
            if (parentConnection == null || !parentConnection.GetGameObject() ||
                !parentConnection.GetGameObject().TryGetNitroxId(out NitroxId parentPipeId))
            {
                Log.Error($"Couldn't find a valid reference to the OxygenPipe's parent pipe");
                return;
            }
            IPipeConnection rootConnection = parentConnection.GetRoot();
            if (rootConnection == null || !rootConnection.GetGameObject() ||
                !rootConnection.GetGameObject().TryGetNitroxId(out NitroxId rootPipeId))
            {
                Log.Error($"Couldn't find a valid reference to the OxygenPipe's root pipe");
                return;
            }

            // Updating the local pipe's references to replace the UniqueIdentifier's id by their NitroxEntity's id
            oxygenPipe.rootPipeUID = rootPipeId.ToString();
            oxygenPipe.parentPipeUID = parentPipeId.ToString();

            droppedItem = new OxygenPipeEntity(gameObject.transform.ToWorldDto(), 0, classId, false, id, techType.Value.ToDto(), metadata.OrNull(), null,
                                              childrenEntities, rootPipeId, parentPipeId, parentConnection.GetAttachPoint().ToDto());
        }
        else
        {
            // Generic case
            droppedItem = new(gameObject.transform.ToWorldDto(), 0, classId, false, id, techType.Value.ToDto(), metadata.OrNull(), null, childrenEntities);
        }

        if (packetSender.Send(new EntitySpawnedByClient(droppedItem, true)))
        {
            Log.Debug($"Dropping item: {droppedItem}");
        }
    }

    /// <summary>
    /// Handles objects placed as figures and posters, or LEDLights so that we can spawn them accordingly afterwards.
    /// </summary>
    public void Placed(GameObject gameObject, TechType techType)
    {
        RemoveAnyRemoteControl(gameObject);

        NitroxId id = NitroxEntity.GetIdOrGenerateNew(gameObject);
        Optional<EntityMetadata> metadata = entityMetadataManager.Extract(gameObject);
        string classId = gameObject.GetComponent<PrefabIdentifier>().ClassId;

        List<Entity> childrenEntities = GetPrefabChildren(gameObject, id, entityMetadataManager).ToList();
        WorldEntity placedItem;

        // If the object is dropped in the water, it'll be parented to a CellRoot so we let it as WorldEntity (see Items.Dropped)
        // PlaceTool's object is located under GlobalRoot or under a CellRoot (we differentiate both by giving a different type)
        // Because objects under CellRoots must only spawn when visible while objects under GlobalRoot must be spawned at all times
        switch (gameObject.AliveOrNull())
        {
            case not null when IsGlobalRootObject(gameObject):
                placedItem = new GlobalRootEntity(gameObject.transform.ToWorldDto(), 0, classId, true, id, techType.ToDto(), metadata.OrNull(), null, childrenEntities);
                break;
            case not null when Player.main.AliveOrNull()?.GetCurrentSub().AliveOrNull()?.TryGetNitroxId(out NitroxId parentId) == true:
                placedItem = new GlobalRootEntity(gameObject.transform.ToLocalDto(), 0, classId, true, id, techType.ToDto(), metadata.OrNull(), parentId, childrenEntities);
                break;
            default:
                // If the object is not under a SubRoot nor in GlobalRoot, it'll be under a CellRoot but we still want to remember its state
                placedItem = new PlacedWorldEntity(gameObject.transform.ToWorldDto(), 0, classId, true, id, techType.ToDto(), metadata.OrNull(), null, childrenEntities);
                break;
        }

        if (packetSender.Send(new EntitySpawnedByClient(placedItem, true)))
        {
            Log.Debug($"Placed object: {placedItem}");
        }
    }

    // This function will record any notable children of the dropped item as a PrefabChildEntity.  In this case, a 'notable'
    // child is one that UWE has tagged with a PrefabIdentifier (class id) and has entity metadata that can be extracted. An
    // example would be recording a Battery PrefabChild inside of a Flashlight WorldEntity.
    public static IEnumerable<Entity> GetPrefabChildren(GameObject gameObject, NitroxId parentId, EntityMetadataManager entityMetadataManager)
    {
        foreach (IGrouping<string, PrefabIdentifier> prefabGroup in gameObject.GetAllComponentsInChildren<PrefabIdentifier>()
                                                                              .Where(prefab => prefab.gameObject != gameObject)
                                                                              .GroupBy(prefab => prefab.classId))
        {
            int indexInGroup = 0;

            foreach (PrefabIdentifier prefab in prefabGroup)
            {
                NitroxId id = NitroxEntity.GetIdOrGenerateNew(prefab.gameObject); // We do this here bc a MetadataExtractor could be requiring the id to increment or so
                Optional<EntityMetadata> metadata = entityMetadataManager.Extract(prefab.gameObject);

                if (metadata.HasValue)
                {
                    TechTag techTag = prefab.gameObject.GetComponent<TechTag>();
                    TechType techType = (techTag) ? techTag.type : TechType.None;

                    yield return new PrefabChildEntity(id, prefab.classId, techType.ToDto(), indexInGroup, metadata.Value, parentId);

                    indexInGroup++;
                }
            }
        }
    }

    /// <summary>
    /// Overloads <see cref="ConvertToInventoryItemEntity"/> and removes any tracking on <paramref name="gameObject"/>
    /// </summary>
    private InventoryItemEntity ConvertToInventoryEntityUntracked(GameObject gameObject, NitroxId parentId)
    {
        InventoryItemEntity inventoryItemEntity = ConvertToInventoryItemEntity(gameObject, parentId, entityMetadataManager);

        // Some picked up entities are not known by the server for several reasons.  First it can be picked up via a spawn item command.  Another
        // example is that some obects are not 'real' objects until they are clicked and end up spawning a prefab.  For example, the fire extinguisher
        // in the escape pod (mono: IntroFireExtinguisherHandTarget) or Creepvine seeds (mono: PickupPrefab).  When clicked, these spawn new prefabs
        // directly into the player's inventory.  These will ultimately be registered server side with the above inventoryItemEntity.
        entities.MarkAsSpawned(inventoryItemEntity);

        // We want to remove any remote tracking immediately on pickup as it can cause weird behavior like holding a ghost item still in the world.
        RemoveAnyRemoteControl(gameObject);
        EntityPositionBroadcaster.StopWatchingEntity(inventoryItemEntity.Id);

        return inventoryItemEntity;
    }

    public static InventoryItemEntity ConvertToInventoryItemEntity(GameObject gameObject, NitroxId parentId, EntityMetadataManager entityMetadataManager)
    {
        NitroxId itemId = NitroxEntity.GetIdOrGenerateNew(gameObject); // id may not exist, create if missing
        string classId = gameObject.RequireComponent<PrefabIdentifier>().ClassId;
        TechType techType = gameObject.RequireComponent<Pickupable>().GetTechType();
        Optional<EntityMetadata> metadata = entityMetadataManager.Extract(gameObject);
        List<Entity> children = GetPrefabChildren(gameObject, itemId, entityMetadataManager).ToList();

        InventoryItemEntity inventoryItemEntity = new(itemId, classId, techType.ToDto(), metadata.OrNull(), parentId, children);
        BatteryChildEntityHelper.TryPopulateInstalledBattery(gameObject, inventoryItemEntity.ChildEntities, itemId);

        return inventoryItemEntity;
    }

    /// <summary>
    /// Some items might be remotely simulated if they were dropped by other players.  We'll want to remove
    /// any remote tracking when we actively handle the item.
    /// </summary>
    private void RemoveAnyRemoteControl(GameObject gameObject)
    {
        UnityEngine.Object.Destroy(gameObject.GetComponent<RemotelyControlled>());
    }

    /// <param name="parent">Parent of the GameObject to check</param>
    public static bool TryGetParentWaterPark(Transform parent, out WaterPark waterPark)
    {
        // NB: When dropped in a WaterPark, items are placed under WaterPark/items_root/
        // So we need to search two steps higher to find the WaterPark
        if (parent && parent.parent && parent.parent.TryGetComponent(out waterPark))
        {
            return true;
        }

        waterPark = null;
        return false;
    }


    /// <inheritdoc cref="TryGetParentWaterPark" />
    private static bool TryGetParentWaterParkId(Transform parent, out NitroxId waterParkId)
    {
        if (TryGetParentWaterPark(parent, out WaterPark waterPark) && waterPark.TryGetNitroxId(out waterParkId))
        {
            return true;
        }

        waterParkId = null;
        return false;
    }

    public static List<InstalledModuleEntity> GetEquipmentModuleEntities(Equipment equipment, NitroxId equipmentId, EntityMetadataManager entityMetadataManager)
    {
        List<InstalledModuleEntity> entities = new();
        foreach (KeyValuePair<string, InventoryItem> itemEntry in equipment.equipment)
        {
            InventoryItem item = itemEntry.Value;
            if (item != null)
            {
                Pickupable pickupable = item.item;
                string classId = pickupable.RequireComponent<PrefabIdentifier>().ClassId;
                NitroxId itemId = NitroxEntity.GetIdOrGenerateNew(pickupable.gameObject);
                Optional<EntityMetadata> metadata = entityMetadataManager.Extract(pickupable.gameObject);
                List<Entity> children = GetPrefabChildren(pickupable.gameObject, itemId, entityMetadataManager).ToList();

                entities.Add(new(itemEntry.Key, classId, itemId, pickupable.GetTechType().ToDto(), metadata.OrNull(), equipmentId, children));
            }
        }
        return entities;
    }

    private static bool IsGlobalRootObject(GameObject gameObject)
    {
        return gameObject.TryGetComponent(out LargeWorldEntity largeWorldEntity) &&
            largeWorldEntity.initialCellLevel == LargeWorldEntity.CellLevel.Global;
    }
}
