using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RimMind.Application.Common.Interfaces.Mechanisms;
using RimMind.Application.Common.Models.Mechanisms;
using RimMind.Application.Common.Models.Tools;
using RimMind.Domain.Enums;
using RimMind.Domain.ValueObjects;
using RimMind.Presentation.Agent;

namespace RimMind.Infrastructure.Mechanisms.World.Dialog
{
    /// <summary>
    /// Popups and letters waiting for the player's answer (<see cref="PendingChoices"/>): query lists them, trigger
    /// <c>choose</c> answers one. World-admin scope so colonist agents never get it; the colony overseer adds these
    /// tools explicitly.
    /// </summary>
    public sealed class DialogChoiceMechanism : GameMechanismBaseNoDef, IMechanismScopeMetadata
    {
        public override string MechanismId => "world.dialog";
        public override MechanismScope Scope => MechanismScope.World;
        public override MechanismRisk Risk => MechanismRisk.Moderate;
        public override IReadOnlyList<MechanismOperationType> SupportedOperations => _supportedOps;
        public override MechanismDocs Docs => _docs;

        public ToolScope? GetToolScopeForOperation(MechanismOperationType operation) => ToolScope.WorldAdmin;

        private static readonly IReadOnlyList<MechanismOperationType> _supportedOps =
            new List<MechanismOperationType> { MechanismOperationType.Query, MechanismOperationType.Trigger }.AsReadOnly();

        private static readonly MechanismDocs _docs = new MechanismDocs
        {
            Summary = "Popups and letters waiting for the player's decision (e.g. a refugee asking to join: Accept / Reject / Jump to location / Postpone).",
            QueryDescription = "List pending choices: id, title, text and numbered options.",
            TriggerDescription = "Answer a pending choice: action 'choose' with choice_id (from the list) and option (1-based number). Runs the option exactly like a player click.",
        };

        private static readonly IReadOnlyList<MechanismActionInfo> _writeActions = new List<MechanismActionInfo>
        {
            new MechanismActionInfo("choose", "Answer a pending popup or letter", parameterDescriptors: new List<MechanismParamDescriptor>
            {
                new MechanismParamDescriptor("choice_id", "string", "Pending choice id, e.g. 'dialog:123' or 'letter:45'", required: true),
                new MechanismParamDescriptor("option", "integer", "1-based number of the option to pick", required: true),
            }.AsReadOnly()),
        }.AsReadOnly();

        public override IReadOnlyList<MechanismActionInfo>? GetWriteActions() => _writeActions;

        public override Task<Result<string, RimMindError>> ExecuteQueryAsync(MechanismReadArgs args, CancellationToken ct)
        {
            var pending = PendingChoices.Collect();
            return Task.FromResult(Result<string, RimMindError>.Ok(pending.Count == 0
                ? "No popup or letter is waiting for a decision."
                : JsonConvert.SerializeObject(pending)));
        }

        public override Task<Result<bool, RimMindError>> ExecuteTriggerAsync(MechanismWriteArgs args, CancellationToken ct)
        {
            string? choiceId = null;
            string? optionRaw = null;
            args.Params?.TryGetValue("choice_id", out choiceId);
            args.Params?.TryGetValue("option", out optionRaw);
            if (!int.TryParse(optionRaw, out int option))
                return Task.FromResult(Result<bool, RimMindError>.Err(RimMindErrors.InvalidAction(MechanismId, "option must be a 1-based number")));

            try
            {
                string outcome = PendingChoices.Choose(choiceId ?? string.Empty, option);
                global::Verse.Log.Message($"[RimMind-Core] world.dialog {choiceId}: {outcome}");
                return Task.FromResult(Result<bool, RimMindError>.Ok(true));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                return Task.FromResult(Result<bool, RimMindError>.Err(RimMindErrors.InvalidAction(MechanismId, ex.Message)));
            }
        }
    }
}
