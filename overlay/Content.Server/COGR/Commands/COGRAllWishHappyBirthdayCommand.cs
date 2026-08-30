using System.Linq;
using System.Text.Json;
using COGR.Core.Identifiers;
using Content.Server.Access.Systems;
using Content.Server.COGR.Systems;
using Content.Shared.COGR.Components;
using Content.Shared.Inventory;
using Content.Shared.PDA;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace Content.Server.COGR.Commands;

/// <summary>
/// P11 stress harness. Seeds the same semantic birthday-wish desire for every active COGR-controlled Coggent.
/// Station deliberately does not choose another person, inspect their identity, choose an action, or render speech.
/// Bare COGR chamber fixtures receive an ordinary PassengerPDA credential so the public-identity perception path
/// can be exercised without falling back to hidden entity metadata.
/// </summary>
public sealed class COGRAllWishHappyBirthdayCommand : IConsoleCommand
{
    private const string RuntimeCommand = "cogr.p11.wish_happy_birthday";
    private const string HarnessPdaPrototype = "PassengerPDA";

    public string Command => "all_wish_happy_birthday";
    public string Description => "Seeds the P11 birthday-wish stress task for every active COGR-controlled agent.";
    public string Help => "all_wish_happy_birthday";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError(Help);
            return;
        }

        var systems = IoCManager.Resolve<IEntitySystemManager>();
        var entityManager = IoCManager.Resolve<IEntityManager>();
        var adapter = systems.GetEntitySystem<COGRAdapterSystem>();
        var authority = systems.GetEntitySystem<COGRBodyAuthorityCoordinatorSystem>();
        var idCardSystem = systems.GetEntitySystem<IdCardSystem>();
        var inventory = systems.GetEntitySystem<InventorySystem>();
        if (adapter.Connection is not { IsConnected: true } connection)
        {
            shell.WriteError("COGR runtime is not connected.");
            return;
        }

        if (adapter.EntityMapper is null)
        {
            shell.WriteError("COGR entity mapper is not initialized.");
            return;
        }

        var connectionId = ConnectionId.FromGuid(connection.ConnectionId);
        var active = adapter.EntityMapper.GetMappingSnapshot()
            .Where(mapping =>
                entityManager.TryGetComponent<COGRControlledComponent>(mapping.Key, out var controlled)
                && controlled.IsActive)
            .Select(mapping => new HarnessParticipant(
                mapping.Key,
                AgentId.FromGuid(mapping.Value)))
            .Where(candidate => authority.ResolveBoundLease(candidate.AgentId, connectionId).HasValue)
            .OrderBy(candidate => candidate.AgentId.ToGuid())
            .ToArray();

        if (active.Length == 0)
        {
            shell.WriteLine("No active COGR-controlled agents with current body authority were found.");
            return;
        }

        var provisioned = 0;
        foreach (var candidate in active)
        {
            if (idCardSystem.TryFindIdCard(candidate.Entity, out _))
                continue;

            if (inventory.TryGetSlotEntity(candidate.Entity, "id", out _))
            {
                shell.WriteError(
                    $"Birthday harness could not provision a public credential for {candidate.AgentId}: the ID slot is occupied by a non-ID item.");
                continue;
            }

            var coordinates = entityManager.GetComponent<TransformComponent>(candidate.Entity).Coordinates;
            var pda = entityManager.SpawnEntity(HarnessPdaPrototype, coordinates);
            if (!entityManager.TryGetComponent<PdaComponent>(pda, out var pdaComponent)
                || pdaComponent.ContainedId is not { } containedId)
            {
                entityManager.DeleteEntity(pda);
                shell.WriteError(
                    $"Birthday harness could not provision a readable PassengerPDA for {candidate.AgentId}.");
                continue;
            }

            var publicName = entityManager.GetComponent<MetaDataComponent>(candidate.Entity).EntityName;
            if (!idCardSystem.TryChangeFullName(containedId, publicName))
            {
                entityManager.DeleteEntity(pda);
                shell.WriteError(
                    $"Birthday harness could not assign the public fixture identity for {candidate.AgentId}.");
                continue;
            }

            if (!inventory.TryEquip(candidate.Entity, pda, "id", silent: true, force: true))
            {
                entityManager.DeleteEntity(pda);
                shell.WriteError(
                    $"Birthday harness could not equip its public credential for {candidate.AgentId}.");
                continue;
            }

            provisioned++;
        }

        var queued = 0;
        foreach (var candidate in active)
        {
            var parameters = JsonSerializer.SerializeToUtf8Bytes(new
            {
                agentId = candidate.AgentId.ToString(),
            });

            try
            {
                _ = connection.SendAdministrativeCommand(RuntimeCommand, parameters);
                queued++;
            }
            catch (Exception ex)
            {
                shell.WriteError($"Failed to queue birthday task for {candidate.AgentId}: {ex.Message}");
            }
        }

        shell.WriteLine(
            $"Queued P11 birthday-wish tasks for {queued}/{active.Length} active COGR agents; " +
            $"provisioned {provisioned} bare chamber fixtures with ordinary PassengerPDA public credentials. " +
            "Each Coggent must independently choose the nearest perceived other person, read a public ID/PDA name if available, remember it, face them, and speak the corpus-rendered greeting.");
    }

    private readonly record struct HarnessParticipant(EntityUid Entity, AgentId AgentId);
}
