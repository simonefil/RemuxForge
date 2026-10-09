using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RemuxForge.Core.Models;

namespace RemuxForge.Core.Splitting
{
    public partial class MkvSplitDocumentService
    {
        #region Metodi pubblici

        /// <summary>
        /// Esegue un comando su una copia profonda. Un errore conserva integralmente l'input.
        /// </summary>
        /// <param name="document">Documento di partenza, mai modificato</param>
        /// <param name="command">Comando da eseguire</param>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <returns>Esito con il documento risultante, la selezione proposta e le diagnostiche</returns>
        public MkvSplitEditResult Execute(MkvSplitDocument document, MkvSplitEditCommand command, MkvSplitAnalysis analysis)
        {
            MkvSplitEditResult result = new MkvSplitEditResult { Document = document };
            result.Diagnostics.AddRange(ValidateStructure(document, analysis));
            if (result.Diagnostics.Count > 0)
                return result;
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
                        if (command.Kind == MkvSplitEditKind.CreateOutputFromSource)
                            created.Clips.Add(SourceClip(command, analysis, "invalidSourceRange"));
                        Insert(candidate.Outputs, command.InsertIndex, new List<MkvSplitOutput> { created });
                        result.SelectedOutputId = created.Id;
                        break;
                    case MkvSplitEditKind.RenameOutput:
                        Require(output != null, "outputMissing");
                        Require(Enum.IsDefined(command.NameMode));
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
                        Require(output != null, "outputMissing");
                        Require(clip != null, "cursorOutsideClip");
                        int local = command.ResultFrame - output.Clips.TakeWhile(item => item.Id != clip.Id).Sum(item => item.EndFrameExclusive - item.StartFrame);
                        Require(local > 0 && local < clip.EndFrameExclusive - clip.StartFrame, "cannotSplitClipHere");
                        int split = SplitAt(output, command.ResultFrame);
                        result.SelectedClipId = output.Clips[split].Id;
                        break;
                    case MkvSplitEditKind.SplitOutput:
                        Require(output != null, "outputMissing");
                        Require(command.ResultFrame > 0 && command.ResultFrame < CountFrames(output), "cannotSplitOutputHere");
                        int boundary = SplitAt(output, command.ResultFrame);
                        created = new MkvSplitOutput { Clips = output.Clips.Skip(boundary).ToList() };
                        output.Clips.RemoveRange(boundary, output.Clips.Count - boundary);
                        candidate.Outputs.Insert(candidate.Outputs.IndexOf(output) + 1, created);
                        result.SelectedOutputId = created.Id;
                        break;
                    case MkvSplitEditKind.InsertSource:
                    case MkvSplitEditKind.AppendSource:
                        Require(output != null, "outputMissing");
                        MkvSplitClip added = SourceClip(command, analysis, "invalidSourceRange");
                        int position = command.Kind == MkvSplitEditKind.AppendSource ? output.Clips.Count : SplitAt(output, command.ResultFrame);
                        output.Clips.Insert(position, added);
                        result.SelectedClipId = added.Id;
                        break;
                    case MkvSplitEditKind.RemoveClips:
                        Require(output != null, "outputMissing");
                        List<MkvSplitClip> selected = SelectedClips(output, command.ClipIds);
                        output.Clips.RemoveAll(item => selected.Contains(item));
                        break;
                    case MkvSplitEditKind.RemoveResultRange:
                        Require(output != null, "outputMissing");
                        Require(command.ResultFrame >= 0 && command.ResultEndFrameExclusive > command.ResultFrame
                            && command.ResultEndFrameExclusive <= CountFrames(output), "invalidResultRange");
                        SplitAt(output, command.ResultEndFrameExclusive);
                        int from = SplitAt(output, command.ResultFrame);
                        int to = SplitAt(output, command.ResultEndFrameExclusive);
                        output.Clips.RemoveRange(from, to - from);
                        break;
                    case MkvSplitEditKind.ReorderClips:
                    case MkvSplitEditKind.MoveClips:
                    case MkvSplitEditKind.CopyClips:
                        Require(output != null, "outputMissing");
                        MkvSplitOutput destination = command.Kind == MkvSplitEditKind.ReorderClips ? output :
                            candidate.Outputs.Find(item => item.Id == command.DestinationOutputId);
                        Require(destination != null, "destinationMissing");
                        List<MkvSplitClip> transfer = SelectedClips(output, command.ClipIds);
                        if (command.Kind == MkvSplitEditKind.ReorderClips)
                            transfer = command.ClipIds.Select(id => transfer.Find(item => item.Id == id)).ToList();
                        if (command.Kind == MkvSplitEditKind.CopyClips)
                            transfer = transfer.Select(item => new MkvSplitClip { StartFrame = item.StartFrame, EndFrameExclusive = item.EndFrameExclusive }).ToList();
                        else
                            output.Clips.RemoveAll(item => transfer.Contains(item));
                        Insert(destination.Clips, command.InsertIndex, transfer);
                        result.SelectedOutputId = destination.Id;
                        break;
                    case MkvSplitEditKind.MergeOutputs:
                        // Qui l'ordine di selezione è l'ordine esplicito di assemblaggio, non quello del sorgente.
                        Require(command.OutputIds != null && command.OutputIds.Count >= 2 && command.OutputIds.Distinct().Count() == command.OutputIds.Count, "mergeNeedsDestination");
                        List<MkvSplitOutput> merge = command.OutputIds.Select(id => candidate.Outputs.Find(item => item.Id == id)).ToList();
                        Require(merge.All(item => item != null), "destinationMissing");
                        Require(merge.All(item => item.Clips.Count > 0), "mergeEmptyOutput");
                        MkvSplitOutput first = merge[0];
                        foreach (MkvSplitOutput item in merge.Skip(1))
                            first.Clips.AddRange(item.Clips);
                        candidate.Outputs.RemoveAll(item => merge.Skip(1).Contains(item));
                        result.SelectedOutputId = first.Id;
                        break;
                    case MkvSplitEditKind.RemoveDivision:
                    case MkvSplitEditKind.MoveSharedBoundary:
                        Require(output != null, "outputMissing");
                        Require(clip != null, "clipMissing");
                        int index = output.Clips.IndexOf(clip);
                        Require(index + 1 < output.Clips.Count, "notContiguous");
                        MkvSplitClip next = output.Clips[index + 1];
                        Require(next.Id == command.NextClipId && clip.EndFrameExclusive == next.StartFrame, "notContiguous");
                        if (command.Kind == MkvSplitEditKind.RemoveDivision)
                        {
                            clip.EndFrameExclusive = next.EndFrameExclusive;
                            output.Clips.RemoveAt(index + 1);
                        }
                        else
                        {
                            Require(command.StartFrame > clip.StartFrame && command.StartFrame < next.EndFrameExclusive, "sharedBoundaryOutside");
                            clip.EndFrameExclusive = command.StartFrame;
                            next.StartFrame = command.StartFrame;
                        }
                        break;
                    case MkvSplitEditKind.TrimClip:
                        Require(clip != null, "clipMissing");
                        SourceClip(command, analysis, "invalidTrimRange");
                        clip.StartFrame = command.StartFrame;
                        clip.EndFrameExclusive = command.EndFrameExclusive;
                        break;
                    default:
                        throw new ArgumentException();
                }
                result.Diagnostics.AddRange(ValidateStructure(candidate, analysis));
                if (result.Diagnostics.Count > 0)
                    return result;
                result.Changed = JsonSerializer.Serialize(document) != JsonSerializer.Serialize(candidate);
                result.Document = result.Changed ? candidate : document;
                result.SelectedOutputId ??= output?.Id;
            }
            catch (CommandRejectedException rejected)
            {
                result.Diagnostics.Add(Diagnostic(rejected.Code, command.OutputId, command.ClipId));
                result.SelectedOutputId = null;
                result.SelectedClipId = null;
            }
            catch (ArgumentException)
            {
                result.Diagnostics.Add(Diagnostic("invalidCommand", command.OutputId, command.ClipId));
                result.SelectedOutputId = null;
                result.SelectedClipId = null;
            }
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Interrompe il comando con una diagnostica specifica se la condizione non è soddisfatta
        /// </summary>
        /// <param name="condition">Condizione richiesta</param>
        /// <param name="code">Codice della diagnostica split.montage.{code}</param>
        private static void Require(bool condition, string code = "invalidCommand")
        {
            if (!condition)
                throw new CommandRejectedException(code);
        }

        /// <summary>
        /// Numero di frame del risultato di un output
        /// </summary>
        /// <param name="output">Output da contare</param>
        /// <returns>Somma dei frame delle clip</returns>
        private static int CountFrames(MkvSplitOutput output)
        {
            return output.Clips.Sum(item => item.EndFrameExclusive - item.StartFrame);
        }

        /// <summary>
        /// Crea una clip dall'intervallo sorgente del comando, dopo averlo validato
        /// </summary>
        /// <param name="command">Comando con StartFrame ed EndFrameExclusive</param>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <param name="code">Codice della diagnostica se l'intervallo non è valido</param>
        /// <returns>Nuova clip</returns>
        private static MkvSplitClip SourceClip(MkvSplitEditCommand command, MkvSplitAnalysis analysis, string code)
        {
            Require(command.StartFrame >= 0 && command.EndFrameExclusive > command.StartFrame && command.EndFrameExclusive <= analysis.SourcePts.Length, code);
            return new MkvSplitClip { StartFrame = command.StartFrame, EndFrameExclusive = command.EndFrameExclusive };
        }

        /// <summary>
        /// Output selezionati, nell'ordine del documento; la selezione deve essere non vuota, senza duplicati e tutta esistente
        /// </summary>
        /// <param name="document">Documento</param>
        /// <param name="ids">Identificativi selezionati</param>
        /// <returns>Output selezionati</returns>
        private static List<MkvSplitOutput> SelectedOutputs(MkvSplitDocument document, List<Guid> ids)
        {
            Require(ids != null && ids.Count > 0 && ids.Distinct().Count() == ids.Count, "outputMissing");
            List<MkvSplitOutput> selected = document.Outputs.Where(item => ids.Contains(item.Id)).ToList();
            Require(selected.Count == ids.Count, "outputMissing");
            return selected;
        }

        /// <summary>
        /// Clip selezionate, nell'ordine dell'output; la selezione deve essere non vuota, senza duplicati e tutta esistente
        /// </summary>
        /// <param name="output">Output</param>
        /// <param name="ids">Identificativi selezionati</param>
        /// <returns>Clip selezionate</returns>
        private static List<MkvSplitClip> SelectedClips(MkvSplitOutput output, List<Guid> ids)
        {
            Require(ids != null && ids.Count > 0 && ids.Distinct().Count() == ids.Count, "noClipSelected");
            List<MkvSplitClip> selected = output.Clips.Where(item => ids.Contains(item.Id)).ToList();
            Require(selected.Count == ids.Count, "noClipSelected");
            return selected;
        }

        /// <summary>
        /// Inserisce gli elementi alla posizione indicata, che deve essere valida
        /// </summary>
        /// <typeparam name="T">Tipo degli elementi</typeparam>
        /// <param name="list">Lista di destinazione</param>
        /// <param name="index">Posizione di inserimento</param>
        /// <param name="items">Elementi da inserire</param>
        private static void Insert<T>(List<T> list, int index, List<T> items)
        {
            Require(index >= 0 && index <= list.Count, "invalidPosition");
            list.InsertRange(index, items);
        }

        /// <summary>
        /// Rende un confine risultato un confine fra clip, preservando l'identità della metà sinistra.
        /// </summary>
        /// <param name="output">Output da dividere</param>
        /// <param name="frame">Confine nel risultato</param>
        /// <returns>Indice della clip che inizia al confine</returns>
        private static int SplitAt(MkvSplitOutput output, int frame)
        {
            Require(frame >= 0 && frame <= CountFrames(output), "cursorOutsideOutput");
            int prefix = 0;
            for (int index = 0; index < output.Clips.Count; index++)
            {
                MkvSplitClip clip = output.Clips[index];
                if (frame == prefix)
                    return index;
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

        #endregion

        #region Tipi annidati

        /// <summary>
        /// Rifiuto di un comando con il codice della diagnostica da mostrare
        /// </summary>
        private sealed class CommandRejectedException : Exception
        {
            #region Costruttore

            /// <summary>
            /// Crea il rifiuto con il codice indicato
            /// </summary>
            /// <param name="code">Codice della diagnostica split.montage.{code}</param>
            public CommandRejectedException(string code)
            {
                this.Code = code;
            }

            #endregion

            #region Proprietà

            /// <summary>
            /// Codice della diagnostica
            /// </summary>
            public string Code { get; }

            #endregion
        }

        #endregion
    }
}
