using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Valida gli output previsti dalla modalità Metadata, sorgente unica per UI e CLI
    /// </summary>
    public static class MetadataOutputValidator
    {
        #region Tipi annidati

        /// <summary>
        /// Tipo di conflitto rilevato sugli output metadata
        /// </summary>
        public enum MetadataOutputConflictKind
        {
            /// <summary>Due record producono lo stesso file di output</summary>
            Collision,

            /// <summary>Il file di output esiste già e la sovrascrittura non è abilitata</summary>
            Exists
        }

        /// <summary>
        /// Conflitto rilevato fra gli output previsti
        /// </summary>
        public class MetadataOutputConflict
        {
            #region Costruttore

            /// <summary>
            /// Costruttore
            /// </summary>
            public MetadataOutputConflict()
            {
                this.InputFile = "";
                this.OutputFile = "";
                this.OtherInputFile = "";
                this.Kind = MetadataOutputConflictKind.Exists;
            }

            #endregion

            #region Proprietà

            /// <summary>
            /// File di input che non potrà essere scritto
            /// </summary>
            public string InputFile { get; set; }

            /// <summary>
            /// File di output in conflitto
            /// </summary>
            public string OutputFile { get; set; }

            /// <summary>
            /// File di input che ha già rivendicato lo stesso output, solo per le collisioni
            /// </summary>
            public string OtherInputFile { get; set; }

            /// <summary>
            /// Tipo di conflitto
            /// </summary>
            public MetadataOutputConflictKind Kind { get; set; }

            #endregion
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Restituisce i conflitti fra gli output previsti, senza interrompere al primo
        /// </summary>
        /// <param name="records">Record da controllare</param>
        /// <param name="options">Opzioni runtime metadata</param>
        /// <returns>Conflitti rilevati, vuoto se non ce ne sono</returns>
        public static List<MetadataOutputConflict> Validate(List<MkvMetadataRecord> records, MkvMetadataOptions options)
        {
            List<MetadataOutputConflict> conflicts = new List<MetadataOutputConflict>();
            Dictionary<string, string> claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            MkvMetadataRecord record;
            string outputFile;

            // In sovrascrittura l'output è il file stesso: non ci sono collisioni da rilevare
            if (records == null || options == null || options.OutputPolicy != MkvMetadataOutputPolicy.OutputPath)
                return conflicts;

            for (int i = 0; i < records.Count; i++)
            {
                record = records[i];
                if (record.AnalysisStatus != MkvMetadataAnalysisStatus.Analyzed || record.ExecutionMode == MkvMetadataExecutionMode.NoOp)
                    continue;

                outputFile = MetadataExecutionService.BuildOutputFile(record, options);

                if (claimed.ContainsKey(outputFile))
                {
                    conflicts.Add(new MetadataOutputConflict
                    {
                        InputFile = record.InputFile,
                        OutputFile = outputFile,
                        OtherInputFile = claimed[outputFile],
                        Kind = MetadataOutputConflictKind.Collision
                    });
                    continue;
                }

                claimed[outputFile] = record.InputFile;

                if (!options.OverwriteOutput && File.Exists(outputFile))
                {
                    conflicts.Add(new MetadataOutputConflict
                    {
                        InputFile = record.InputFile,
                        OutputFile = outputFile,
                        Kind = MetadataOutputConflictKind.Exists
                    });
                }
            }

            return conflicts;
        }

        /// <summary>
        /// Restituisce il messaggio localizzato di un conflitto
        /// </summary>
        /// <param name="conflict">Conflitto rilevato</param>
        /// <returns>Messaggio localizzato</returns>
        public static string DescribeConflict(MetadataOutputConflict conflict)
        {
            if (conflict.Kind == MetadataOutputConflictKind.Collision)
                return Localization.AppText.F("metadata.error.outputCollision", conflict.OutputFile);

            return Localization.AppText.F("metadata.error.outputExists", conflict.OutputFile);
        }

        #endregion
    }
}
