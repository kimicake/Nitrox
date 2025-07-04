using NitroxClient.Communication.Packets.Processors.Abstract;
using NitroxClient.GameLogic;
using NitroxClient.GameLogic.PlayerLogic;
using NitroxClient.MonoBehaviours;
using NitroxModel.Packets;
using UnityEngine;

namespace NitroxClient.Communication.Packets.Processors;

public class EntityDestroyedProcessor : ClientPacketProcessor<EntityDestroyed>
{
    public const DamageType DAMAGE_TYPE_RUN_ORIGINAL = (DamageType)100;

    private readonly Entities entities;

    public EntityDestroyedProcessor(Entities entities)
    {
        this.entities = entities;
    }

    public override void Process(EntityDestroyed packet)
    {
        entities.RemoveEntity(packet.Id);
        if (!NitroxEntity.TryGetObjectFrom(packet.Id, out GameObject gameObject))
        {
            entities.MarkForDeletion(packet.Id);
            Log.Warn($"[{nameof(EntityDestroyedProcessor)}] Could not find entity with id: {packet.Id} to destroy.");
            return;
        }

        using (PacketSuppressor<EntityDestroyed>.Suppress())
        {
            // This type of check could get out of control if there are many types with custom destroy logic. If we get a couple more, move to separate processors.
            if (gameObject.TryGetComponent(out Vehicle vehicle))
            {
                DestroyVehicle(vehicle);
            }
            else if (gameObject.TryGetComponent(out SubRoot subRoot))
            {
                DestroySubroot(subRoot);
            }
            else if (gameObject.TryGetComponent(out Pickupable pickupable))
            {
                DestroyPickupable(pickupable);
            }
            else
            {
                Entities.DestroyObject(gameObject);
            }
        }
    }

    private void DestroyVehicle(Vehicle vehicle)
    {
        if (vehicle.GetPilotingMode()) //Check Local Object Have Player inside
        {
            vehicle.OnPilotModeEnd();

            if (!Player.main.ToNormalMode(true))
            {
                Player.main.ToNormalMode(false);
                Player.main.transform.parent = null;
            }
        }

        foreach (RemotePlayerIdentifier identifier in vehicle.GetComponentsInChildren<RemotePlayerIdentifier>(true))
        {
            identifier.RemotePlayer.ResetStates();
        }

        if (vehicle.gameObject)
        {
            if (vehicle.destructionEffect)
            {
                GameObject gameObject = Object.Instantiate(vehicle.destructionEffect);
                gameObject.transform.position = vehicle.transform.position;
                gameObject.transform.rotation = vehicle.transform.rotation;
            }

            Object.Destroy(vehicle.gameObject);
        }
    }

    private void DestroySubroot(SubRoot subRoot)
    {
        DamageInfo damageInfo = new() { type = DAMAGE_TYPE_RUN_ORIGINAL };
        if (subRoot.live.health > 0f)
        {
            // oldHPPercent must be in the interval [0; 0.25[ because else, SubRoot.OnTakeDamage will end up in the wrong else condition
            subRoot.oldHPPercent = 0f;
            subRoot.live.health = 0f;
            subRoot.live.NotifyAllAttachedDamageReceivers(damageInfo);
            subRoot.live.Kill();
        }

        // We use a specific DamageType so that the Prefix on this method will accept this call
        subRoot.OnTakeDamage(damageInfo);
    }

    private void DestroyPickupable(Pickupable pickupable)
    {
        // The OnDestroy method on Pickupable can send extra EntityDestroyed packets if the item is in an Equipment container, causing a loop.
        // The packet suppressor does not help since destroying an object is not synchronous and only happens at the end of a frame.
        // Calling OnDestroy now means the offending SetInventoryItem call only runs once and additional packets are suppressed.

        pickupable.OnDestroy();
        Object.Destroy(pickupable.gameObject);
    }
}
