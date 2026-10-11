using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting;
using RemuxForge.Core.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace RemuxForge.Core.Media
{
    /// <summary>
    /// Rimuxa in un Matroska temporaneo i file che la pipeline non legge in modo nativo
    /// </summary>
    public class MediaContainerNormalizer
    {
        #region Costanti

        /// <summary>
        /// CodecID Matroska della modalità di compatibilità Video for Windows
        /// </summary>
        private const string VFW_CODEC_ID = "V_MS/VFW/FOURCC";

        /// <summary>
        /// Exit code mkvmerge per esecuzione riuscita con avvisi
        /// </summary>
        private const int MKVMERGE_WARNING_EXIT_CODE = 1;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso eseguibile mkvmerge
        /// </summary>
        private readonly string _mkvMergePath;

        /// <summary>
        /// Lettore delle tracce, sullo stesso mkvmerge della normalizzazione
        /// </summary>
        private readonly MkvToolsService _mkvTools;

        /// <summary>
        /// Cartella dei file temporanei
        /// </summary>
        private readonly string _tempFolder;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore; fuori dai test si passa da Create, che fissa la cartella temporanea della configurazione
        /// </summary>
        /// <param name="mkvMergePath">Percorso eseguibile mkvmerge</param>
        /// <param name="tempFolder">Cartella dei file temporanei</param>
        internal MediaContainerNormalizer(string mkvMergePath, string tempFolder)
        {
            this._mkvMergePath = mkvMergePath;
            this._tempFolder = tempFolder;
            this._mkvTools = new MkvToolsService(mkvMergePath);
        }

        #endregion

        #region Metodi pubblici statici

        /// <summary>
        /// Normalizzatore con il mkvmerge risolto dalla configurazione e la cartella temporanea della configurazione
        /// </summary>
        /// <returns>Normalizzatore</returns>
        public static MediaContainerNormalizer Create()
        {
            return Create(new ToolPathResolverService(AppSettingsService.Instance.ConfigFolder).ResolveMkvMergePath(false));
        }

        /// <summary>
        /// Normalizzatore con il mkvmerge scelto dal chiamante, per esempio quello delle opzioni della pipeline, e la cartella temporanea della configurazione
        /// </summary>
        /// <param name="mkvMergePath">Percorso eseguibile mkvmerge</param>
        /// <returns>Normalizzatore</returns>
        public static MediaContainerNormalizer Create(string mkvMergePath)
        {
            return new MediaContainerNormalizer(mkvMergePath, AppSettingsService.Instance.GetTempFolder());
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Restituisce un Matroska equivalente al file: l'originale se è già MKV, altrimenti un temporaneo
        /// </summary>
        /// <param name="filePath">File da normalizzare</param>
        /// <param name="requireNativeMpeg4">True per rimuxare anche gli MKV con MPEG-4 Part 2 in modalità Video for Windows</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>File normalizzato; con errore il percorso è vuoto e non c'è nulla da cancellare</returns>
        public NormalizedMediaFile Normalize(string filePath, bool requireNativeMpeg4, CancellationToken cancellationToken)
        {
            List<TrackInfo> sourceTracks = this.ReadTracks(filePath);
            bool vfwMpeg4 = sourceTracks.Exists(t => t.Codec == MkvSplitCodecCapabilities.MPEG4_PART2_CODEC && t.CodecId == VFW_CODEC_ID);
            if (MediaContainerFormats.IsMatroska(filePath) && !(requireNativeMpeg4 && vfwMpeg4))
            {
                return new NormalizedMediaFile(filePath, filePath, false, "");
            }

            string tempPath = Path.Combine(this._tempFolder, "normalized_" + Guid.NewGuid().ToString("N") + "_" + Path.GetFileNameWithoutExtension(filePath) + MediaContainerFormats.OUTPUT_EXTENSION);
            ConsoleHelper.Write(LogSection.General, LogLevel.Info, AppText.F("media.normalize.start", Path.GetFileName(filePath)));
            Stopwatch watch = Stopwatch.StartNew();
            // native_mpeg4 porta MPEG-4 Part 2 fuori da Video for Windows con timestamp di presentazione puliti. Solo con una traccia
            // MPEG-4 Part 2: mkvmerge applica la modalità nativa anche a MS-MPEG4v2 (MP42), che non ha configurazione e fallisce
            List<string> arguments = new List<string> { "-q", "-o", tempPath, filePath };
            if (sourceTracks.Exists(t => t.Codec == MkvSplitCodecCapabilities.MPEG4_PART2_CODEC))
            {
                arguments.InsertRange(1, new string[] { "--engage", "native_mpeg4" });
            }

            ProcessResult result = ProcessRunner.Run(this._mkvMergePath, arguments.ToArray(), 0, cancellationToken);
            if (result.ExitCode != 0 && result.ExitCode != MKVMERGE_WARNING_EXIT_CODE)
            {
                FileHelper.DeleteTempFile(tempPath);
                // Un processo interrotto da annullamento o stop non lascia messaggi propri
                string reason = (result.Stdout + result.Stderr).Trim();
                if (cancellationToken.IsCancellationRequested || reason.Length == 0)
                {
                    reason = AppText.T("media.normalize.cancelled");
                }

                return new NormalizedMediaFile(filePath, "", false, AppText.F("media.normalize.failed", Path.GetFileName(filePath), reason));
            }

            // Selezioni e mappature usano gli ID letti sull'originale: il temporaneo deve avere le stesse tracce con gli stessi ID
            List<TrackInfo> normalizedTracks = this.ReadTracks(tempPath);
            if (sourceTracks.Count == 0 || sourceTracks.Count != normalizedTracks.Count || sourceTracks.FindIndex(t => normalizedTracks.FindIndex(n => n.Id == t.Id && n.Type == t.Type) < 0) >= 0)
            {
                FileHelper.DeleteTempFile(tempPath);
                return new NormalizedMediaFile(filePath, "", false, AppText.F("media.normalize.failed", Path.GetFileName(filePath), AppText.T("media.normalize.tracksChanged")));
            }

            ConsoleHelper.Write(LogSection.General, LogLevel.Info, AppText.F("media.normalize.done", Path.GetFileName(filePath), watch.Elapsed.TotalSeconds.ToString("0.0"), (new FileInfo(tempPath).Length / 1048576.0).ToString("0.0")));
            return new NormalizedMediaFile(filePath, tempPath, true, "");
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Legge le tracce con mkvmerge senza token: la lettura dura pochi istanti e l'annullamento lo osservano mkvmerge e il chiamante,
        /// così un temporaneo appena scritto non resta orfano di un'eccezione
        /// </summary>
        /// <param name="filePath">File da leggere</param>
        /// <returns>Tracce del file, lista vuota se mkvmerge non risponde</returns>
        private List<TrackInfo> ReadTracks(string filePath)
        {
            return this._mkvTools.GetFileInfo(filePath, 0, CancellationToken.None)?.Tracks ?? new List<TrackInfo>();
        }

        #endregion
    }

    /// <summary>
    /// Matroska da leggere al posto del file originale; il temporaneo viene cancellato al Dispose
    /// </summary>
    public sealed class NormalizedMediaFile : IDisposable
    {
        #region Variabili di classe

        /// <summary>
        /// True dopo la cancellazione del temporaneo
        /// </summary>
        private bool _disposed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="originalPath">File scelto dall'utente</param>
        /// <param name="path">Matroska da leggere</param>
        /// <param name="isTemporary">True se il Matroska è un temporaneo da cancellare</param>
        /// <param name="error">Motivo del fallimento, vuoto se riuscita</param>
        public NormalizedMediaFile(string originalPath, string path, bool isTemporary, string error)
        {
            this.OriginalPath = originalPath;
            this.Path = path;
            this.IsTemporary = isTemporary;
            this.Error = error;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// File scelto dall'utente, da mostrare nei messaggi
        /// </summary>
        public string OriginalPath { get; }

        /// <summary>
        /// Matroska da leggere
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// True se il Matroska è un temporaneo creato dalla normalizzazione
        /// </summary>
        public bool IsTemporary { get; }

        /// <summary>
        /// Motivo del fallimento, vuoto se la normalizzazione è riuscita
        /// </summary>
        public string Error { get; }

        /// <summary>
        /// True se il Matroska è disponibile
        /// </summary>
        public bool Success => this.Error.Length == 0;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Cancella il temporaneo, se esiste
        /// </summary>
        public void Dispose()
        {
            if (!this._disposed && this.IsTemporary)
            {
                FileHelper.DeleteTempFile(this.Path);
            }

            this._disposed = true;
        }

        #endregion
    }
}
