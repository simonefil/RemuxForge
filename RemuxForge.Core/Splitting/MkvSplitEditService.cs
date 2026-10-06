using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RemuxForge.Core.Models;

namespace RemuxForge.Core.Splitting
{
    public partial class MkvSplitDocumentService
    {
        /// <summary>Esegue un comando su una copia profonda. Un errore conserva integralmente l'input.</summary>
        public MkvSplitEditResult Execute(MkvSplitDocument document, MkvSplitEditCommand command, MkvSplitAnalysis analysis)
        {
            MkvSplitEditResult result = new MkvSplitEditResult { Document = document };
            result.Diagnostics.AddRange(ValidateStructure(document, analysis));
            if (result.Diagnostics.Count > 0) return result;
            if (command == null || !Enum.IsDefined(command.Kind))
            {
                result.Diagnostics.Add(Diagnostic("invalidCommand"));
                return result;
            }
            MkvSplitDocument candidate = document.Clone();
            try
            {
                MkvSplitOutput output = candidate.Outputs.Find(item => item.Id == command.OutputId);
                MkvSplitClip clip = output?.Clips.Find(item => item.Id == command.ClipId);
                MkvSplitOutput created;
                switch (command.Kind)
                {
                    case MkvSplitEditKind.CreateEmptyOutput:
                    case MkvSplitEditKind.CreateOutputFromSource:
                        created = new MkvSplitOutput();
                        if (command.Kind == MkvSplitEditKind.CreateOutputFromSource) created.Clips.Add(SourceClip(command, analysis));
                        Insert(candidate.Outputs, command.InsertIndex, new List<MkvSplitOutput> { created });
                        result.SelectedOutputId = created.Id;
                        break;
                    case MkvSplitEditKind.RenameOutput:
                        Require(output != null && Enum.IsDefined(command.NameMode));
                        output.NameMode = command.NameMode;
                        output.CustomFileName = command.NameMode == MkvSplitNameMode.Custom ? command.CustomFileName : "";
                        break;
                    case MkvSplitEditKind.RemoveOutputs:
                        List<MkvSplitOutput> removed = SelectedOutputs(candidate, command.OutputIds);
                        candidate.Outputs.RemoveAll(item => removed.Contains(item));
                        break;
                    case MkvSplitEditKind.ReorderOutputs:
                        List<MkvSplitOutput> ordered = SelectedOutputs(candidate, command.OutputIds);
                        candidate.Outputs.RemoveAll(item => ordered.Contains(item));
                        Insert(candidate.Outputs, command.InsertIndex, ordered);
                        break;
                    case MkvSplitEditKind.SplitClip:
                        Require(output != null && clip != null);
                        int local = command.ResultFrame - output.Clips.TakeWhile(item => item.Id != clip.Id).Sum(item => item.EndFrameExclusive - item.StartFrame);
                        Require(local > 0 && local < clip.EndFrameExclusive - clip.StartFrame);
                        int split = SplitAt(output, command.ResultFrame);
                        result.SelectedClipId = output.Clips[split].Id;
                        break;
                    case MkvSplitEditKind.SplitOutput:
                        Require(output != null);
                        Require(command.ResultFrame > 0 && command.ResultFrame < CountFrames(output));
                        int boundary = SplitAt(output, command.ResultFrame);
                        created = new MkvSplitOutput { Clips = output.Clips.Skip(boundary).ToList() };
                        output.Clips.RemoveRange(boundary, output.Clips.Count - boundary);
                        candidate.Outputs.Insert(candidate.Outputs.IndexOf(output) + 1, created);
                        result.SelectedOutputId = created.Id;
                        break;
                    case MkvSplitEditKind.InsertSource:
                    case MkvSplitEditKind.AppendSource:
                        Require(output != null);
                        MkvSplitClip added = SourceClip(command, analysis);
                        int position = command.Kind == MkvSplitEditKind.AppendSource ? output.Clips.Count : SplitAt(output, command.ResultFrame);
                        output.Clips.Insert(position, added);
                        result.SelectedClipId = added.Id;
                        break;
                    case MkvSplitEditKind.RemoveClips:
                        Require(output != null);
                        List<MkvSplitClip> selected = SelectedClips(output, command.ClipIds);
                        output.Clips.RemoveAll(item => selected.Contains(item));
                        break;
                    case MkvSplitEditKind.RemoveResultRange:
                        Require(output != null && command.ResultFrame >= 0 && command.ResultEndFrameExclusive > command.ResultFrame
                            && command.ResultEndFrameExclusive <= CountFrames(output));
                        SplitAt(output, command.ResultEndFrameExclusive);
                        int from = SplitAt(output, command.ResultFrame);
                        int to = SplitAt(output, command.ResultEndFrameExclusive);
                        output.Clips.RemoveRange(from, to - from);
                        break;
                    case MkvSplitEditKind.ReorderClips:
                    case MkvSplitEditKind.MoveClips:
                    case MkvSplitEditKind.CopyClips:
                        Require(output != null);
                        MkvSplitOutput destination = command.Kind == MkvSplitEditKind.ReorderClips ? output :
                            candidate.Outputs.Find(item => item.Id == command.DestinationOutputId);
                        Require(destination != null);
                        List<MkvSplitClip> transfer = SelectedClips(output, command.ClipIds);
                        if (command.Kind == MkvSplitEditKind.ReorderClips)
                            transfer = command.ClipIds.Select(id => transfer.Find(item => item.Id == id)).ToList();
                        if (command.Kind == MkvSplitEditKind.CopyClips)
                            transfer = transfer.Select(item => new MkvSplitClip { StartFrame = item.StartFrame, EndFrameExclusive = item.EndFrameExclusive }).ToList();
                        else output.Clips.RemoveAll(item => transfer.Contains(item));
                        Insert(destination.Clips, command.InsertIndex, transfer);
                        result.SelectedOutputId = destination.Id;
                        break;
                    case MkvSplitEditKind.MergeOutputs:
                        // Ici l'ordre de sélection est l'ordre explicite d'assemblage, pas celui du sorgente.
                        Require(command.OutputIds != null && command.OutputIds.Count >= 2 && command.OutputIds.Distinct().Count() == command.OutputIds.Count);
                        List<MkvSplitOutput> merge = command.OutputIds.Select(id => candidate.Outputs.Find(item => item.Id == id)).ToList();
                        Require(merge.All(item => item != null && item.Clips.Count > 0));
                        MkvSplitOutput first = merge[0];
                        foreach (MkvSplitOutput item in merge.Skip(1)) first.Clips.AddRange(item.Clips);
                        candidate.Outputs.RemoveAll(item => merge.Skip(1).Contains(item));
                        result.SelectedOutputId = first.Id;
                        break;
                    case MkvSplitEditKind.RemoveDivision:
                    case MkvSplitEditKind.MoveSharedBoundary:
                        Require(output != null && clip != null);
                        int index = output.Clips.IndexOf(clip);
                        Require(index + 1 < output.Clips.Count);
                        MkvSplitClip next = output.Clips[index + 1];
                        Require(next.Id == command.NextClipId && clip.EndFrameExclusive == next.StartFrame);
                        if (command.Kind == MkvSplitEditKind.RemoveDivision)
                        {
                            clip.EndFrameExclusive = next.EndFrameExclusive;
                            output.Clips.RemoveAt(index + 1);
                        }
                        else
                        {
                            Require(command.StartFrame > clip.StartFrame && command.StartFrame < next.EndFrameExclusive);
                            clip.EndFrameExclusive = command.StartFrame;
                            next.StartFrame = command.StartFrame;
                        }
                        break;
                    case MkvSplitEditKind.TrimClip:
                        Require(clip != null);
                        SourceClip(command, analysis);
                        clip.StartFrame = command.StartFrame;
                        clip.EndFrameExclusive = command.EndFrameExclusive;
                        break;
                    default: throw new ArgumentException();
                }
                result.Diagnostics.AddRange(ValidateStructure(candidate, analysis));
                if (result.Diagnostics.Count > 0) return result;
                result.Changed = JsonSerializer.Serialize(document) != JsonSerializer.Serialize(candidate);
                result.Document = result.Changed ? candidate : document;
                result.SelectedOutputId ??= output?.Id;
            }
            catch (ArgumentException)
            {
                result.Diagnostics.Add(Diagnostic("invalidCommand", command.OutputId, command.ClipId));
                result.SelectedOutputId = null;
                result.SelectedClipId = null;
            }
            return result;
        }

        private static void Require(bool condition) { if (!condition) throw new ArgumentException(); }

        private static int CountFrames(MkvSplitOutput output)
        {
            return output.Clips.Sum(item => item.EndFrameExclusive - item.StartFrame);
        }

        private static MkvSplitClip SourceClip(MkvSplitEditCommand command, MkvSplitAnalysis analysis)
        {
            Require(command.StartFrame >= 0 && command.EndFrameExclusive > command.StartFrame && command.EndFrameExclusive <= analysis.SourcePts.Length);
            return new MkvSplitClip { StartFrame = command.StartFrame, EndFrameExclusive = command.EndFrameExclusive };
        }

        private static List<MkvSplitOutput> SelectedOutputs(MkvSplitDocument document, List<Guid> ids)
        {
            Require(ids != null && ids.Count > 0 && ids.Distinct().Count() == ids.Count);
            List<MkvSplitOutput> selected = document.Outputs.Where(item => ids.Contains(item.Id)).ToList();
            Require(selected.Count == ids.Count);
            return selected;
        }

        private static List<MkvSplitClip> SelectedClips(MkvSplitOutput output, List<Guid> ids)
        {
            Require(ids != null && ids.Count > 0 && ids.Distinct().Count() == ids.Count);
            List<MkvSplitClip> selected = output.Clips.Where(item => ids.Contains(item.Id)).ToList();
            Require(selected.Count == ids.Count);
            return selected;
        }

        private static void Insert<T>(List<T> list, int index, List<T> items)
        {
            Require(index >= 0 && index <= list.Count);
            list.InsertRange(index, items);
        }

        /// <summary>Rende un confine risultato un confine fra clip, preservando l'identità della metà sinistra.</summary>
        private static int SplitAt(MkvSplitOutput output, int frame)
        {
            Require(frame >= 0 && frame <= CountFrames(output));
            int prefix = 0;
            for (int index = 0; index < output.Clips.Count; index++)
            {
                MkvSplitClip clip = output.Clips[index];
                if (frame == prefix) return index;
                int end = prefix + clip.EndFrameExclusive - clip.StartFrame;
                if (frame < end)
                {
                    int boundary = clip.StartFrame + frame - prefix;
                    MkvSplitClip right = new MkvSplitClip { StartFrame = boundary, EndFrameExclusive = clip.EndFrameExclusive };
                    clip.EndFrameExclusive = boundary;
                    output.Clips.Insert(index + 1, right);
                    return index + 1;
                }
                prefix = end;
            }
            return output.Clips.Count;
        }
    }
}
