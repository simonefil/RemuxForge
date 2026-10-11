using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
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
        /// Codec mkvmerge di MPEG-4 Part 2 (Xvid, DivX 4 e successivi)
        /// </summary>
        public const string MPEG4_PART2_CODEC = "MPEG-4p2";

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
        /// Cartella dei file temporanei
        /// </summary>
        private readonly string _tempFolder;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="mkvMergePath">Percorso eseguibile mkvmerge</param>
        /// <param name="tempFolder">Cartella dei file temporanei</param>
        public MediaContainerNormalizer(string mkvMergePath, string tempFolder)
        {
            this._mkvMergePath = mkvMergePath;
            this._tempFolder = tempFolder;
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
            List<string[]> sourceTracks = this.ReadTracks(filePath, cancellationToken);
            bool vfwMpeg4 = sourceTracks.Exists(t => t[2] == MPEG4_PART2_CODEC && t[3] == VFW_CODEC_ID);
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
            if (sourceTracks.Exists(t => t[2] == MPEG4_PART2_CODEC))
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
            List<string[]> normalizedTracks = this.ReadTracks(tempPath, cancellationToken);
            if (sourceTracks.Count == 0 || sourceTracks.Count != normalizedTracks.Count || sourceTracks.FindIndex(t => normalizedTracks.FindIndex(n => n[0] == t[0] && n[1] == t[1]) < 0) >= 0)
            {
                FileHelper.DeleteTempFile(tempPath);
                return new NormalizedMediaFile(filePath, "", false, AppText.F("media.normalize.failed", Path.GetFileName(filePath), AppText.T("media.normalize.tracksChanged")));
            }

            ConsoleHelper.Write(LogSection.General, LogLevel.Info, AppText.F("media.normalize.done", Path.GetFileName(filePath), watch.Elapsed.TotalSeconds.ToString("0.0"), (new FileInfo(tempPath).Length / 1048576.0).ToString("0.0")));
            return new NormalizedMediaFile(filePath, tempPath, true, "");
        }

        /// <summary>
        /// Normalizza il file Source e poi, se indicato e solo se il primo è riuscito, il file Lang
        /// </summary>
        /// <param name="sourcePath">File Source</param>
        /// <param name="langPath">File Lang, vuoto per non normalizzarlo</param>
        /// <param name="requireNativeMpeg4">True per rimuxare anche gli MKV con MPEG-4 Part 2 in modalità Video for Windows</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <param name="source">Source normalizzato</param>
        /// <param name="lang">Lang normalizzato, null se non richiesto o se il Source è fallito</param>
        /// <returns>Motivo del primo fallimento, vuoto se tutto è riuscito</returns>
        public string NormalizePair(string sourcePath, string langPath, bool requireNativeMpeg4, CancellationToken cancellationToken, out NormalizedMediaFile source, out NormalizedMediaFile lang)
        {
            source = this.Normalize(sourcePath, requireNativeMpeg4, cancellationToken);
            lang = source.Success && !string.IsNullOrEmpty(langPath) ? this.Normalize(langPath, requireNativeMpeg4, cancellationToken) : null;
            return !source.Success ? source.Error : lang != null && !lang.Success ? lang.Error : "";
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Legge le tracce con mkvmerge come righe id, tipo, codec e CodecID
        /// </summary>
        /// <returns>Una riga per traccia, lista vuota se mkvmerge non risponde</returns>
        private List<string[]> ReadTracks(string filePath, CancellationToken cancellationToken)
        {
            List<string[]> tracks = new List<string[]>();
            ProcessResult result = ProcessRunner.Run(this._mkvMergePath, new string[] { "-J", filePath }, 0, cancellationToken);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
            {
                return tracks;
            }

            using JsonDocument document = JsonDocument.Parse(result.Stdout);
            if (!document.RootElement.TryGetProperty("tracks", out JsonElement elements))
            {
                return tracks;
            }

            foreach (JsonElement track in elements.EnumerateArray())
            {
                string codecId = track.TryGetProperty("properties", out JsonElement properties) && properties.TryGetProperty("codec_id", out JsonElement codecIdElement) ? codecIdElement.GetString() : "";
                tracks.Add(new string[] { track.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture), track.GetProperty("type").GetString(), track.GetProperty("codec").GetString(), codecId });
            }

            return tracks;
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
