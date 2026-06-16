using RemuxForge.Core.Models;
using System.IO;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Riscrive sottotitoli VobSub IDX/SUB applicando cut e insert ai timestamp e ai filepos
    /// </summary>
    internal class VobSubSubtitleTimelineRewriter
    {
        #region Metodi pubblici

        /// <summary>
        /// Riscrive una coppia IDX/SUB applicando le operazioni dell'edit map
        /// </summary>
        /// <param name="inputIdx">File IDX originale</param>
        /// <param name="inputSub">File SUB originale</param>
        /// <param name="outputIdx">File IDX riscritto</param>
        /// <param name="outputSub">File SUB riscritto</param>
        /// <param name="editMap">Edit map da applicare</param>
        /// <returns>True se la coppia riscritta contiene entry valide</returns>
        public bool Rewrite(string inputIdx, string inputSub, string outputIdx, string outputSub, EditMap editMap)
        {
            VobSubIndexDocument document = VobSubIndexDocument.Load(inputIdx);
            byte[] subData = File.ReadAllBytes(inputSub);
            MemoryStream subOutput = new MemoryStream();
            VobSubIndexEntry entry;
            long mappedMs;
            long nextFilePosition;
            long outputPosition;
            int keptCount = 0;

            // IDX contiene i timestamp e i filepos, SUB contiene i packet bitmap collegati
            if (document.Entries.Count == 0)
            {
                return false;
            }

            // Mappa timestamp, rimuove le entry tagliate e compatta il SUB in parallelo
            for (int i = 0; i < document.Entries.Count; i++)
            {
                entry = document.Entries[i];
                mappedMs = SubtitleTimelineMapper.MapPacketTimestamp(entry.TimestampMs, editMap);
                if (mappedMs < 0)
                {
                    document.RemoveEntry(entry);
                    continue;
                }

                nextFilePosition = i + 1 < document.Entries.Count ? document.Entries[i + 1].FilePosition : subData.Length;
                if (entry.FilePosition < 0 || nextFilePosition < entry.FilePosition || nextFilePosition > subData.Length)
                {
                    return false;
                }

                outputPosition = subOutput.Position;
                subOutput.Write(subData, (int)entry.FilePosition, (int)(nextFilePosition - entry.FilePosition));
                document.RewriteEntry(entry, mappedMs, outputPosition);
                keptCount++;
            }

            // Nessuna entry valida significa sottotitolo completamente tagliato
            if (keptCount == 0)
            {
                return false;
            }

            document.Save(outputIdx);
            File.WriteAllBytes(outputSub, subOutput.ToArray());
            return true;
        }

        #endregion
    }
}
