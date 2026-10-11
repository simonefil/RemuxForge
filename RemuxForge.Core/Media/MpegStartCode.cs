using System;

namespace RemuxForge.Core.Media
{
    /// <summary>
    /// Ricerca degli start code MPEG (00 00 01 seguito dal byte del codice) nei flussi elementari e nei pacchetti PES
    /// </summary>
    public static class MpegStartCode
    {
        #region Metodi pubblici

        /// <summary>
        /// Cerca in avanti il primo start code con il codice indicato
        /// </summary>
        /// <param name="data">Buffer da scandire</param>
        /// <param name="from">Offset iniziale; un valore negativo vale zero</param>
        /// <param name="code">Byte che segue 00 00 01</param>
        /// <returns>Offset dello start code, -1 se assente</returns>
        public static int Find(byte[] data, int from, byte code)
        {
            return Find(data, from, data.Length, value => value == code);
        }

        /// <summary>
        /// Cerca in avanti il primo start code il cui codice soddisfa il filtro, con i quattro byte entro il limite
        /// </summary>
        /// <param name="data">Buffer da scandire</param>
        /// <param name="from">Offset iniziale; un valore negativo vale zero</param>
        /// <param name="end">Limite esclusivo dei quattro byte dello start code</param>
        /// <param name="accepts">Filtro sul byte che segue 00 00 01</param>
        /// <returns>Offset dello start code, -1 se assente</returns>
        public static int Find(byte[] data, int from, int end, Predicate<byte> accepts)
        {
            for (int i = Math.Max(0, from); i + 4 <= end; i++)
            {
                if (data[i] == 0x00 && data[i + 1] == 0x00 && data[i + 2] == 0x01 && accepts(data[i + 3]))
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Cerca all'indietro, da un offset fino a un limite inferiore, lo start code con il codice indicato
        /// </summary>
        /// <param name="data">Buffer da scandire</param>
        /// <param name="from">Primo offset controllato</param>
        /// <param name="lowest">Offset più basso controllato; un valore negativo vale zero</param>
        /// <param name="code">Byte che segue 00 00 01</param>
        /// <returns>Offset dello start code, -1 se assente</returns>
        public static int FindLast(byte[] data, int from, int lowest, byte code)
        {
            for (int i = Math.Min(from, data.Length - 4); i >= Math.Max(0, lowest); i--)
            {
                if (data[i] == 0x00 && data[i + 1] == 0x00 && data[i + 2] == 0x01 && data[i + 3] == code)
                    return i;
            }

            return -1;
        }

        #endregion
    }
}
