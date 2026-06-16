using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Applica una EditMap alle tracce sottotitoli riscrivendo i timestamp nel formato nativo
    /// </summary>
    public class SubtitleTimelineEditService
    {
        #region Variabili di classe

        /// <summary>
        /// Percorso ffmpeg usato per estrazione/riscrittura testuale
        /// </summary>
        private readonly string _ffmpegPath;

        /// <summary>
        /// Cartella temporanea per file sottotitolo estratti o riscritti
        /// </summary>
        private readonly string _tempFolder;

        /// <summary>
        /// Timeout dei processi esterni
        /// </summary>
        private readonly int _timeoutMs;

        /// <summary>
        /// Percorso mkvmerge effettivamente usato dalla pipeline
        /// </summary>
        private readonly string _mkvMergePath;

        /// <summary>
        /// Risolutore centralizzato per i tool path
        /// </summary>
        private readonly ToolPathResolverService _toolPathResolver;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="ffmpegPath">Percorso ffmpeg</param>
        /// <param name="tempFolder">Cartella temporanea</param>
        /// <param name="timeoutMs">Timeout operazioni esterne</param>
        /// <param name="mkvMergePath">Percorso mkvmerge gia' risolto dalla pipeline</param>
        /// <param name="toolPathResolver">Resolver strumenti esterni</param>
        public SubtitleTimelineEditService(string ffmpegPath, string tempFolder, int timeoutMs, string mkvMergePath = "", ToolPathResolverService toolPathResolver = null)
        {
            this._ffmpegPath = ffmpegPath;
            this._tempFolder = tempFolder;
            this._timeoutMs = timeoutMs;
            this._mkvMergePath = mkvMergePath != null ? mkvMergePath : "";
            this._toolPathResolver = toolPathResolver ?? new ToolPathResolverService(AppSettingsService.Instance.ConfigFolder);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Applica l'EditMap a una traccia sottotitoli
        /// </summary>
        /// <param name="langFile">File lingua di origine</param>
        /// <param name="trackId">ID traccia sottotitoli</param>
        /// <param name="trackCodec">Codec sottotitoli</param>
        /// <param name="editMap">Edit map da applicare</param>
        /// <param name="label">Etichetta temporanea</param>
        /// <returns>Path del file sottotitolo riscritto, vuoto se non applicabile</returns>
        public string Apply(string langFile, int trackId, string trackCodec, EditMap editMap, string label)
        {
            string result = "";
            if (this.IsSrtCodec(trackCodec))
            {
                result = this.ApplyTextSubtitle(langFile, trackId, editMap, label, true);
            }
            else if (this.IsAssCodec(trackCodec))
            {
                result = this.ApplyTextSubtitle(langFile, trackId, editMap, label, false);
            }
            else if (this.IsPgsCodec(trackCodec))
            {
                result = this.ApplyPgsSubtitle(langFile, trackId, editMap, label);
            }
            else if (this.IsVobSubCodec(trackCodec))
            {
                result = this.ApplyVobSubSubtitle(langFile, trackId, editMap, label);
            }
            else
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "  Codec sottotitoli non supportato per riscrittura timestamp: " + trackCodec);
            }

            return result;
        }

        #endregion

        #region Metodi privati - Formati testuali

        /// <summary>
        /// Estrae una traccia testuale, riscrive i timestamp e produce il file temporaneo muxabile
        /// </summary>
        /// <param name="langFile">File lingua di origine</param>
        /// <param name="trackId">ID traccia sottotitoli</param>
        /// <param name="editMap">Edit map da applicare</param>
        /// <param name="label">Etichetta temporanea</param>
        /// <param name="srt">True per SRT, false per ASS/SSA</param>
        /// <returns>Path del sottotitolo riscritto, oppure stringa vuota</returns>
        private string ApplyTextSubtitle(string langFile, int trackId, EditMap editMap, string label, bool srt)
        {
            string result = "";
            string extension = srt ? ".srt" : ".ass";
            string inputFile = Path.Combine(this._tempFolder, label + "_sub_t" + trackId + "_src_" + Guid.NewGuid().ToString("N").Substring(0, 8) + extension);
            string outputFile = Path.Combine(this._tempFolder, label + "_deep_t" + trackId + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + extension);
            string codec = srt ? "srt" : "ass";
            string content;
            string rewritten;
            int exitCode;

            // ffmpeg normalizza l'estrazione testuale in SRT/ASS prima della riscrittura timestamp
            exitCode = this.RunFfmpeg(new string[]
            {
                "-nostdin", "-hide_banner", "-y",
                "-i", langFile,
                "-map", "0:" + trackId.ToString(CultureInfo.InvariantCulture),
                "-c:s", codec,
                inputFile
            });

            if (exitCode != 0 || !File.Exists(inputFile))
            {
                FileHelper.DeleteTempFile(inputFile);
                return result;
            }

            content = File.ReadAllText(inputFile);
            if (srt)
            {
                // SRT e ASS hanno parser separati per preservare il formato nativo dei timestamp
                SrtSubtitleTimelineRewriter rewriter = new SrtSubtitleTimelineRewriter();
                rewritten = rewriter.Rewrite(content, editMap);
            }
            else
            {
                AssSubtitleTimelineRewriter rewriter = new AssSubtitleTimelineRewriter();
                rewritten = rewriter.Rewrite(content, editMap);
            }
            File.WriteAllText(outputFile, rewritten, new UTF8Encoding(false));
            FileHelper.DeleteTempFile(inputFile);

            if (this.ValidateSubtitleFile(outputFile))
            {
                result = outputFile;
            }
            else
            {
                FileHelper.DeleteTempFile(outputFile);
            }

            return result;
        }

        #endregion

        #region Metodi privati - PGS/VobSub

        /// <summary>
        /// Estrae e riscrive una traccia PGS mantenendo il payload bitmap originale
        /// </summary>
        /// <param name="langFile">File lingua di origine</param>
        /// <param name="trackId">ID traccia sottotitoli</param>
        /// <param name="editMap">Edit map da applicare</param>
        /// <param name="label">Etichetta temporanea</param>
        /// <returns>Path del file SUP riscritto, oppure stringa vuota</returns>
        private string ApplyPgsSubtitle(string langFile, int trackId, EditMap editMap, string label)
        {
            string result = "";
            string inputFile = Path.Combine(this._tempFolder, label + "_sub_t" + trackId + "_src_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sup");
            string outputFile = Path.Combine(this._tempFolder, label + "_deep_t" + trackId + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sup");

            if (!this.ExtractWithMkvExtract(langFile, trackId, inputFile))
            {
                return result;
            }

            // Il rewriter modifica solo timestamp/segmenti PGS; le immagini non vengono ricodificate
            PgsSubtitleTimelineRewriter rewriter = new PgsSubtitleTimelineRewriter();
            if (rewriter.Rewrite(inputFile, outputFile, editMap) && File.Exists(outputFile) && this.ValidateSubtitleFile(outputFile))
            {
                result = outputFile;
            }
            else
            {
                FileHelper.DeleteTempFile(outputFile);
            }

            FileHelper.DeleteTempFile(inputFile);
            return result;
        }

        /// <summary>
        /// Estrae e riscrive una traccia VobSub mantenendo coppia IDX/SUB coerente
        /// </summary>
        /// <param name="langFile">File lingua di origine</param>
        /// <param name="trackId">ID traccia sottotitoli</param>
        /// <param name="editMap">Edit map da applicare</param>
        /// <param name="label">Etichetta temporanea</param>
        /// <returns>Path del file IDX riscritto, oppure stringa vuota</returns>
        private string ApplyVobSubSubtitle(string langFile, int trackId, EditMap editMap, string label)
        {
            string result = "";
            string inputIdx = Path.Combine(this._tempFolder, label + "_sub_t" + trackId + "_src_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".idx");
            string inputSub = Path.ChangeExtension(inputIdx, ".sub");
            string outputIdx = Path.Combine(this._tempFolder, label + "_deep_t" + trackId + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".idx");
            string outputSub = Path.ChangeExtension(outputIdx, ".sub");

            if (!this.ExtractWithMkvExtract(langFile, trackId, inputIdx) || !File.Exists(inputSub))
            {
                FileHelper.DeleteTempFile(inputIdx);
                FileHelper.DeleteTempFile(inputSub);
                return result;
            }

            // IDX contiene i timestamp, SUB contiene i pacchetti bitmap: entrambi vanno mantenuti allineati
            VobSubSubtitleTimelineRewriter rewriter = new VobSubSubtitleTimelineRewriter();
            if (rewriter.Rewrite(inputIdx, inputSub, outputIdx, outputSub, editMap) && this.ValidateSubtitleFile(outputIdx))
            {
                result = outputIdx;
            }
            else
            {
                FileHelper.DeleteTempFile(outputIdx);
                FileHelper.DeleteTempFile(outputSub);
            }

            FileHelper.DeleteTempFile(inputIdx);
            FileHelper.DeleteTempFile(inputSub);
            return result;
        }

        #endregion

        #region Metodi privati - Utility

        /// <summary>
        /// Verifica che un sottotitolo generato sia leggibile da ffmpeg
        /// </summary>
        /// <param name="filePath">Path sottotitolo da validare</param>
        /// <returns>True se ffmpeg riesce a demuxare la traccia</returns>
        private bool ValidateSubtitleFile(string filePath)
        {
            ProcessResult result = ProcessRunner.Run(this._ffmpegPath, new string[]
            {
                "-nostdin",
                "-v", "error",
                "-i", filePath,
                "-map", "0:0",
                "-c", "copy",
                "-f", "null",
                "-"
            }, this._timeoutMs);

            return result != null && result.ExitCode == 0;
        }

        /// <summary>
        /// Estrae una traccia sottotitoli bitmap con mkvextract
        /// </summary>
        /// <param name="langFile">File lingua di origine</param>
        /// <param name="trackId">ID traccia da estrarre</param>
        /// <param name="outputFile">File destinazione</param>
        /// <returns>True se l'estrazione produce il file richiesto</returns>
        private bool ExtractWithMkvExtract(string langFile, int trackId, string outputFile)
        {
            string mkvExtractPath = this.ResolveMkvExtractPath();
            if (mkvExtractPath.Length == 0)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "  mkvextract non disponibile per sottotitoli bitmap");
                return false;
            }

            // mkvextract usa la sintassi trackId:path e produce anche il .sub accanto al .idx per VobSub
            ProcessResult result = ProcessRunner.Run(mkvExtractPath, new string[]
            {
                "tracks",
                langFile,
                trackId.ToString(CultureInfo.InvariantCulture) + ":" + outputFile
            }, this._timeoutMs);

            return result != null && result.ExitCode == 0 && File.Exists(outputFile);
        }

        /// <summary>
        /// Risolve mkvextract partendo dal provider mkvmerge esistente
        /// </summary>
        /// <returns>Path mkvextract, oppure stringa vuota</returns>
        private string ResolveMkvExtractPath()
        {
            string mkvMergePath = this._mkvMergePath;
            if (mkvMergePath.Length == 0)
            {
                mkvMergePath = this._toolPathResolver.ResolveMkvMergePath(false);
            }

            return this._toolPathResolver.ResolveMkvExtractPath(mkvMergePath, false);
        }

        /// <summary>
        /// Determina se il codec rappresenta una traccia SRT/SubRip
        /// </summary>
        /// <param name="codec">Codec dichiarato dal contenitore</param>
        /// <returns>True per SRT/SubRip</returns>
        private bool IsSrtCodec(string codec)
        {
            string c = codec != null ? codec.ToLowerInvariant() : "";
            return c.Contains("subrip") || c.Contains("s_text/utf8") || c.Contains("utf-8") || c == "srt";
        }

        /// <summary>
        /// Determina se il codec rappresenta una traccia ASS/SSA
        /// </summary>
        /// <param name="codec">Codec dichiarato dal contenitore</param>
        /// <returns>True per ASS/SSA</returns>
        private bool IsAssCodec(string codec)
        {
            string c = codec != null ? codec.ToLowerInvariant() : "";
            return c.Contains("substation alpha") ||
                c.Contains("substationalpha") ||
                c.Contains("s_text/ass") ||
                c.Contains("s_text/ssa") ||
                c == "ass" ||
                c == "ssa";
        }

        /// <summary>
        /// Determina se il codec rappresenta una traccia PGS
        /// </summary>
        /// <param name="codec">Codec dichiarato dal contenitore</param>
        /// <returns>True per PGS</returns>
        private bool IsPgsCodec(string codec)
        {
            string c = codec != null ? codec.ToLowerInvariant() : "";
            return c.Contains("pgs") || c.Contains("s_hdmv/pgs");
        }

        /// <summary>
        /// Determina se il codec rappresenta una traccia VobSub
        /// </summary>
        /// <param name="codec">Codec dichiarato dal contenitore</param>
        /// <returns>True per VobSub/DVD subtitle</returns>
        private bool IsVobSubCodec(string codec)
        {
            string c = codec != null ? codec.ToLowerInvariant() : "";
            return c.Contains("vobsub") || c.Contains("s_vobsub") || c.Contains("dvd subtitle");
        }

        /// <summary>
        /// Esegue ffmpeg tramite ProcessRunner normalizzando gli argomenti composti
        /// </summary>
        /// <param name="args">Argomenti ffmpeg</param>
        /// <returns>Exit code processo</returns>
        private int RunFfmpeg(string[] args)
        {
            string[] splitArgs = ProcessRunner.SplitCompoundArgs(args);
            return ProcessRunner.RunDiscardOutput(this._ffmpegPath, splitArgs, this._timeoutMs);
        }

        #endregion
    }
}
