using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting.Cutters;
using System;
using System.IO;

namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// Codec tagliato a byte: testa ricodificata in un elementary stream e corpo copiato a livello di byte dal flusso grezzo, preceduto
    /// dai parameter set originali
    /// </summary>
    public abstract class MkvSplitByteCodecBase : MkvSplitExactCutCodecBase
    {
        #region Costanti

        /// <summary>
        /// Blocco di lettura del prefisso del flusso grezzo (1 MB)
        /// </summary>
        private const int READ_BLOCK_SIZE = 1 << 20;

        #endregion

        #region Proprietà

        /// <summary>
        /// Taglio a byte
        /// </summary>
        public override MkvSplitExactCutMethod Method => MkvSplitExactCutMethod.Bytes;

        /// <summary>
        /// Estensione dell'elementary stream grezzo, senza punto
        /// </summary>
        public abstract string RawExtension { get; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scrive l'intestazione che precede la parte copiata a byte: i parameter set originali. Serve perché il corpo riparte da
        /// un keyframe interno al flusso grezzo e senza parameter set il decoder non ha extradata. I parameter set precedono il primo
        /// fotogramma: se ne legge a blocchi solo il prefisso, fino al primo fotogramma (che chiude l'ultimo parameter set) o alla
        /// fine del file, perché il flusso grezzo di un episodio intero può superare i 2 GB che un array in memoria ammette
        /// </summary>
        /// <param name="rawFile">Elementary stream grezzo originale</param>
        /// <param name="outputFile">File di output con l'intestazione</param>
        public void WriteParameterSets(string rawFile, string outputFile)
        {
            byte[] data = new byte[0];
            byte[] buf = new byte[READ_BLOCK_SIZE];
            bool ready = false;
            int read;
            FileStream inF = null;

            try
            {
                inF = new FileStream(rawFile, FileMode.Open, FileAccess.Read, FileShare.Read, READ_BLOCK_SIZE, FileOptions.SequentialScan);
                while (!ready && (read = inF.Read(buf, 0, buf.Length)) > 0)
                {
                    Array.Resize(ref data, data.Length + read);
                    Buffer.BlockCopy(buf, 0, data, data.Length - read, read);
                    ready = this.ReachesFirstPicture(data);
                }
            }
            finally
            {
                if (inF != null) { inF.Dispose(); }
            }

            this.WriteParameterSets(data, outputFile);
        }

        /// <summary>
        /// Strategia del taglio a byte
        /// </summary>
        /// <returns>Strategia nuova sul codec</returns>
        public override MkvSplitExactCutterBase CreateCutter()
        {
            return new MkvSplitByteExactCutter(this);
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Indica se il prefisso letto arriva al primo fotogramma, dopo l'ultimo parameter set
        /// </summary>
        /// <param name="data">Prefisso del flusso grezzo</param>
        /// <returns>True se il prefisso contiene l'inizio del primo fotogramma</returns>
        protected abstract bool ReachesFirstPicture(byte[] data);

        /// <summary>
        /// Scrive i parameter set letti nel prefisso del flusso grezzo
        /// </summary>
        /// <param name="data">Prefisso del flusso grezzo</param>
        /// <param name="outputFile">File di output con l'intestazione</param>
        /// <exception cref="InvalidOperationException">Parameter set assenti o incompleti</exception>
        protected abstract void WriteParameterSets(byte[] data, string outputFile);

        #endregion
    }
}
