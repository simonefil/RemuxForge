using RemuxForge.Core.Models;
using System;
using System.Globalization;
using System.IO;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Riscrive canvas e display area di sottotitoli VobSub IDX/SUB
    /// </summary>
    internal class VobSubSubtitleCanvasRewriter : ISubtitleCanvasRewriter
    {
        #region Metodi pubblici

        /// <summary>
        /// Indica se il rewriter gestisce la traccia
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        /// <returns>True se il codec e' VobSub</returns>
        public bool CanHandle(TrackInfo track)
        {
            string codec = track != null && track.Codec != null ? track.Codec.ToLowerInvariant() : "";
            return codec.Contains("vobsub") || codec.Contains("s_vobsub") || codec.Contains("dvd subtitle");
        }

        /// <summary>
        /// Restituisce l'estensione del file principale gestito dal rewriter
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        /// <returns>Estensione IDX</returns>
        public string GetPrimaryExtension(TrackInfo track)
        {
            return ".idx";
        }

        /// <summary>
        /// Riscrive la coppia IDX/SUB estratta
        /// </summary>
        /// <param name="context">Contesto riscrittura</param>
        /// <param name="track">Traccia sottotitoli</param>
        /// <param name="inputFile">File IDX input</param>
        /// <param name="outputFile">File IDX output</param>
        /// <param name="result">Risultato riscrittura</param>
        /// <returns>True se riscrittura riuscita</returns>
        public bool Rewrite(SubtitleCanvasRewriteContext context, TrackInfo track, string inputFile, string outputFile, out SubtitleCanvasRewriteResult result)
        {
            string inputSub = Path.ChangeExtension(inputFile, ".sub");
            string outputSub = Path.ChangeExtension(outputFile, ".sub");
            VobSubIndexDocument document;
            byte[] subData;
            MemoryStream subOutput = new MemoryStream();
            VobSubIndexEntry entry;
            long nextFilePosition;
            long outputPosition;
            byte[] block;
            byte[] rewrittenBlock;
            int areas;
            int decoded;
            int scaled;
            int encoded;
            string errorMessage;

            result = new SubtitleCanvasRewriteResult();
            result.Format = "VobSub";

            if (context == null || context.Transform == null)
            {
                result.ErrorMessage = "contesto canvas VobSub mancante";
                return false;
            }

            if (!File.Exists(inputFile) || !File.Exists(inputSub))
            {
                result.ErrorMessage = "coppia IDX/SUB VobSub incompleta";
                return false;
            }

            document = VobSubIndexDocument.Load(inputFile);
            subData = File.ReadAllBytes(inputSub);

            // IDX guida il taglio dei blocchi SUB: senza entry timestamp/filepos non si puo' ricostruire la coppia
            if (document.Entries.Count == 0)
            {
                result.ErrorMessage = "IDX VobSub senza entry";
                return false;
            }

            // Il canvas dichiarato nell'IDX deve coincidere con la geometria lang analizzata
            if (document.Width > 0 && document.Height > 0 &&
                (document.Width != context.Transform.InputCanvasWidth || document.Height != context.Transform.InputCanvasHeight))
            {
                result.ErrorMessage = "IDX VobSub size " + document.Width.ToString(CultureInfo.InvariantCulture) + "x" + document.Height.ToString(CultureInfo.InvariantCulture) +
                    " non coerente con canvas lang " + context.Transform.InputCanvasWidth.ToString(CultureInfo.InvariantCulture) + "x" + context.Transform.InputCanvasHeight.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            // Dopo la riscrittura le coordinate sono assolute nel canvas output: org/scale/align vanno normalizzati
            document.SetSize(context.Transform.OutputCanvasWidth, context.Transform.OutputCanvasHeight);
            document.SetOrg(0, 0);
            document.SetScale(100, 100);
            document.SetAlign("OFF at LEFT TOP");

            // Copia ogni blocco SUB compattandolo e aggiornando filepos IDX
            for (int i = 0; i < document.Entries.Count; i++)
            {
                entry = document.Entries[i];
                nextFilePosition = i + 1 < document.Entries.Count ? document.Entries[i + 1].FilePosition : subData.Length;
                if (entry.FilePosition < 0 || nextFilePosition < entry.FilePosition || nextFilePosition > subData.Length)
                {
                    result.ErrorMessage = "filepos VobSub fuori SUB";
                    return false;
                }

                // Ogni entry IDX punta all'inizio di un blocco SUB; la prossima entry ne determina la fine
                block = new byte[nextFilePosition - entry.FilePosition];
                Array.Copy(subData, (int)entry.FilePosition, block, 0, block.Length);
                if (!VobSubSubtitleUtils.TryRewriteSubtitleBlock(block, context.Transform, out rewrittenBlock, out areas, out decoded, out scaled, out encoded, out errorMessage))
                {
                    result.ErrorMessage = errorMessage;
                    return false;
                }

                // Scrive il blocco nel SUB compatto e aggiorna il filepos della stessa entry IDX
                outputPosition = subOutput.Position;
                subOutput.Write(rewrittenBlock, 0, rewrittenBlock.Length);
                document.RewriteEntry(entry, entry.TimestampMs, outputPosition);
                result.Increment("entries");
                result.Add("areas", areas);
                result.Add("bitmap-decoded", decoded);
                result.Add("bitmap-scaled", scaled);
                result.Add("bitmap-encoded", encoded);
            }

            File.WriteAllBytes(outputSub, subOutput.ToArray());
            document.Save(outputFile);
            result.Summary = "entries=" + result.Get("entries").ToString(CultureInfo.InvariantCulture) +
                ", SET_DAREA=" + result.Get("areas").ToString(CultureInfo.InvariantCulture) +
                ", bitmap=" + result.Get("bitmap-decoded").ToString(CultureInfo.InvariantCulture) +
                "/" + result.Get("bitmap-scaled").ToString(CultureInfo.InvariantCulture) +
                "/" + result.Get("bitmap-encoded").ToString(CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>
        /// Valida output IDX/SUB in modo leggero
        /// </summary>
        /// <param name="context">Contesto riscrittura</param>
        /// <param name="outputFile">File IDX output</param>
        /// <returns>True se coppia presente e filepos leggibili</returns>
        public bool ValidateOutput(SubtitleCanvasRewriteContext context, string outputFile)
        {
            string subFile = Path.ChangeExtension(outputFile, ".sub");
            VobSubIndexDocument document;
            FileInfo subInfo;

            // La traccia VobSub e' sempre coppia IDX/SUB: entrambi i file devono esistere
            if (!File.Exists(outputFile) || !File.Exists(subFile))
            {
                return false;
            }

            document = VobSubIndexDocument.Load(outputFile);
            subInfo = new FileInfo(subFile);

            // Le entry IDX devono puntare a posizioni reali nel SUB prodotto
            if (document.Entries.Count == 0 || subInfo.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < document.Entries.Count; i++)
            {
                if (document.Entries[i].FilePosition < 0 || document.Entries[i].FilePosition >= subInfo.Length)
                {
                    return false;
                }
            }

            return true;
        }

        #endregion
    }
}
