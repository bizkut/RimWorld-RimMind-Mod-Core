using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMind.Presentation.Agent
{
    /// <summary>One answer of a pending choice; <see cref="Index"/> is 1-based as shown to the model.</summary>
    public sealed class PendingChoiceOption
    {
        public int Index;
        public string Text = string.Empty;
        public bool Disabled;
        public string? DisabledReason;
    }

    /// <summary>A popup or letter waiting for the player's answer.</summary>
    public sealed class PendingChoice
    {
        /// <summary><c>dialog:&lt;window id&gt;</c> or <c>letter:&lt;letter id&gt;</c>.</summary>
        public string Id = string.Empty;
        public string Title = string.Empty;
        public string Text = string.Empty;
        public List<PendingChoiceOption> Options = new List<PendingChoiceOption>();
    }

    /// <summary>
    /// Finds popups and letters that wait for a player decision — event dialogs (plain <see cref="Dialog_NodeTree"/>,
    /// e.g. "Accept / Reject / Jump to location / Postpone") and decision letters (joiners, visitors, ransom, …) — and
    /// answers them by running the option's own handler, exactly as a click would. Confirmation boxes
    /// (<see cref="Dialog_MessageBox"/>: quit, overwrite, delete, other mods' prompts) and specialised node-tree dialogs
    /// (subclasses such as faction negotiation) are deliberately never exposed. Main thread only.
    /// </summary>
    public static class PendingChoices
    {
        /// <summary>
        /// True while an autonomous decision-maker (the colony overseer) answers choices; harness code must then
        /// leave choice dialogs open instead of closing them.
        /// </summary>
        public static bool AutoAnswerActive { get; set; }

        private static readonly FieldInfo? CurNodeField = AccessTools.Field(typeof(Dialog_NodeTree), "curNode");
        private static readonly FieldInfo? OptionTextField = AccessTools.Field(typeof(DiaOption), "text");
        private static readonly FieldInfo? OptionDialogField = AccessTools.Field(typeof(DiaOption), "dialog");
        private static readonly MethodInfo? ActivateMethod = AccessTools.Method(typeof(DiaOption), "Activate");
        private static readonly Regex Tags = new Regex("<[^>]+>", RegexOptions.Compiled);

        /// <summary>Open choice dialogs; when none is open, unopened letters that ask for a decision.</summary>
        public static List<PendingChoice> Collect()
        {
            var result = new List<PendingChoice>();
            var windows = Find.WindowStack?.Windows;
            if (windows != null)
            {
                foreach (var window in windows)
                {
                    if (IsEventDialog(window) && window is Dialog_NodeTree tree && CurNodeField?.GetValue(tree) is DiaNode node
                        && node.options != null && node.options.Count > 0)
                    {
                        result.Add(new PendingChoice
                        {
                            Id = $"dialog:{tree.ID}",
                            Title = Clean(tree.optionalTitle),
                            Text = Clean(node.text),
                            Options = node.options.Select((o, i) => new PendingChoiceOption
                            {
                                Index = i + 1, Text = OptionText(o), Disabled = o.disabled, DisabledReason = o.disabledReason,
                            }).ToList(),
                        });
                    }
                }
            }
            if (result.Count > 0) return result;

            foreach (var letter in Find.LetterStack?.LettersListForReading ?? new List<Letter>())
            {
                // Standard letters only offer "OK / Jump to location"; some letters need their own selection screen.
                if (letter is not ChoiceLetter choiceLetter || letter is StandardLetter || letter is ChoiceLetter_ChoosePawn
                    || letter is ChoiceLetter_BabyBirth || letter is ChoiceLetter_BabyToChild || letter is ChoiceLetter_GameEnded)
                    continue;
                var options = choiceLetter.Choices?.ToList();
                if (options == null || options.Count < 2) continue;
                result.Add(new PendingChoice
                {
                    Id = $"letter:{letter.ID}",
                    Title = Clean(letter.Label),
                    Text = Clean(choiceLetter.Text),
                    Options = options.Select((o, i) => new PendingChoiceOption
                    {
                        Index = i + 1, Text = OptionText(o), Disabled = o.disabled, DisabledReason = o.disabledReason,
                    }).ToList(),
                });
            }
            return result;
        }

        /// <summary>Answer a pending choice with option <paramref name="optionIndex"/> (1-based). Returns what happened.</summary>
        public static string Choose(string choiceId, int optionIndex)
        {
            if (!UnityData.IsInMainThread) throw new InvalidOperationException("choices can only be answered on the main thread");
            if (string.IsNullOrWhiteSpace(choiceId)) throw new ArgumentException("choice_id is required");

            var parts = choiceId.Trim().Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int id))
                throw new ArgumentException($"unknown choice_id '{choiceId}'; use an id from the pending choices list");

            switch (parts[0])
            {
                case "letter":
                {
                    var letter = Find.LetterStack?.LettersListForReading.FirstOrDefault(l => l.ID == id) as ChoiceLetter
                        ?? throw new ArgumentException($"letter {id} is no longer waiting");
                    letter.OpenLetter();
                    var tree = Find.WindowStack.Windows.LastOrDefault(IsEventDialog) as Dialog_NodeTree
                        ?? throw new InvalidOperationException($"letter {id} did not open a choice dialog");
                    return ChooseInTree(tree, optionIndex);
                }
                case "dialog":
                {
                    var tree = Find.WindowStack.Windows.FirstOrDefault(w => w.ID == id && IsEventDialog(w)) as Dialog_NodeTree
                        ?? throw new ArgumentException($"dialog {id} is no longer open");
                    return ChooseInTree(tree, optionIndex);
                }
                default:
                    throw new ArgumentException($"unknown choice_id '{choiceId}'");
            }
        }

        /// <summary>Close a choice without answering it (letters stay in the letter stack).</summary>
        public static void Dismiss(string choiceId)
        {
            var parts = choiceId.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int id)) return;
            var window = Find.WindowStack?.Windows.FirstOrDefault(w => w.ID == id);
            if (window != null) Find.WindowStack!.TryRemove(window, doCloseSound: false);
        }

        private static string ChooseInTree(Dialog_NodeTree tree, int optionIndex)
        {
            var node = CurNodeField?.GetValue(tree) as DiaNode
                ?? throw new InvalidOperationException("dialog has no current page");
            if (optionIndex < 1 || optionIndex > node.options.Count)
                throw new ArgumentException($"option must be 1..{node.options.Count}");
            var option = node.options[optionIndex - 1];
            if (option.disabled)
                throw new InvalidOperationException($"option '{OptionText(option)}' is disabled: {option.disabledReason}");
            if (ActivateMethod == null)
                throw new InvalidOperationException("this RimWorld version does not expose DiaOption.Activate");

            string chosen = OptionText(option);
            OptionDialogField?.SetValue(option, tree);
            ActivateMethod.Invoke(option, null);

            if (!Find.WindowStack.IsOpen(tree))
                return $"chose '{chosen}'; dialog closed";
            var next = CurNodeField?.GetValue(tree) as DiaNode;
            return next == null || next == node
                ? $"chose '{chosen}'"
                : $"chose '{chosen}'; the dialog continues: \"{Trim(Clean(next.text), 300)}\" options: "
                  + string.Join(" | ", next.options.Select((o, i) => $"{i + 1}) {OptionText(o)}"));
        }

        /// <summary>Only the plain event/letter dialog type; subclasses are specialised UIs (e.g. negotiation).</summary>
        private static bool IsEventDialog(Window window) => window.GetType() == typeof(Dialog_NodeTree);

        private static string OptionText(DiaOption option) => OptionTextField?.GetValue(option) switch
        {
            TaggedString tagged => Clean(tagged),
            string text => Clean(text),
            _ => string.Empty,
        };

        private static string Clean(TaggedString text) => Clean(text.Resolve());

        private static string Clean(string? text) =>
            string.IsNullOrEmpty(text) ? string.Empty : Tags.Replace(text!, string.Empty).Trim();

        private static string Trim(string s, int max) => s.Length > max ? s.Substring(0, max) + "…" : s;
    }
}
