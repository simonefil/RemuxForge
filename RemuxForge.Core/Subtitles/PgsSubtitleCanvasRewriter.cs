using System;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Riscrive canvas e coordinate di sottotitoli PGS/SUP senza decodificare le bitmap
    /// </summary>
    internal class PgsSubtitleCanvasRewriter
    {
        #region Metodi pubblici

        /// <summary>
        /// Riscrive PCS/WDS di un file SUP usando il piano indicato
        /// </summary>
        /// <param name="inputFile">File SUP di input</param>
        /// <param name="outputFile">File SUP di output</param>
        /// <param name="plan">Piano canvas/coordinate</param>
        /// <param name="report">Report della riscrittura</param>
        /// <returns>True se il file riscritto e' valido strutturalmente</returns>
        public bool Rewrite(string inputFile, string outputFile, PgsCanvasRewritePlan plan, out PgsCanvasRewriteReport report)
        {
            byte[] data = File.ReadAllBytes(inputFile);
            MemoryStream output = new MemoryStream();
            int pos = 0;
            int setStart;
            int setEnd;

            report = new PgsCanvasRewriteReport();
            while (pos + PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE <= data.Length)
            {
                setStart = pos;
                if (!this.TryFindDisplaySetEnd(data, setStart, out setEnd))
                {
                    report.ErrorMessage = "display-set PGS incompleto";
                    return false;
                }

                report.DisplaySets++;
                if (!this.RewriteDisplaySet(data, setStart, setEnd, plan, output, report))
                {
                    return false;
                }

                pos = setEnd;
            }

            File.WriteAllBytes(outputFile, output.ToArray());
            return output.Length > 0 && report.PcsSegments > 0;
        }

        #endregion

        #region Metodi privati - Display-set

        /// <summary>
        /// Trova la fine del display-set PGS corrente
        /// </summary>
        /// <param name="data">Buffer SUP</param>
        /// <param name="start">Offset iniziale display-set</param>
        /// <param name="end">Offset subito dopo il display-set</param>
        /// <returns>True se il display-set e' completo</returns>
        private bool TryFindDisplaySetEnd(byte[] data, int start, out int end)
        {
            int pos = start;
            int packetLength;
            int segmentType;
            end = start;

            while (pos + PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE <= data.Length)
            {
                if (!PgsSubtitleUtils.TryGetPacketLength(data, pos, out packetLength))
                {
                    return false;
                }

                segmentType = data[pos + 10];
                pos += packetLength;
                if (segmentType == PgsSubtitleUtils.SEGMENT_END)
                {
                    end = pos;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Riscrive i packet rilevanti di un display-set
        /// </summary>
        /// <param name="data">Buffer SUP completo</param>
        /// <param name="start">Offset iniziale display-set</param>
        /// <param name="end">Offset finale display-set</param>
        /// <param name="plan">Piano canvas/coordinate</param>
        /// <param name="output">Output SUP</param>
        /// <param name="report">Report aggiornato</param>
        /// <returns>True se il display-set e' riscritto correttamente</returns>
        private bool RewriteDisplaySet(byte[] data, int start, int end, PgsCanvasRewritePlan plan, MemoryStream output, PgsCanvasRewriteReport report)
        {
            Dictionary<int, PgsObjectSize> objectSizes = this.CollectObjectSizes(data, start, end);
            PgsDisplaySetAdjustment adjustment;
            int pos = start;
            int packetLength;
            int segmentType;
            byte[] packet;

            if (!this.TryResolveDisplaySetAdjustment(data, start, end, plan, objectSizes, report, out adjustment))
            {
                return false;
            }

            while (pos < end)
            {
                if (!PgsSubtitleUtils.TryGetPacketLength(data, pos, out packetLength) || pos + packetLength > end)
                {
                    report.ErrorMessage = "packet PGS fuori display-set";
                    return false;
                }

                packet = new byte[packetLength];
                Array.Copy(data, pos, packet, 0, packetLength);
                segmentType = packet[10];
                if (segmentType == PgsSubtitleUtils.SEGMENT_PRESENTATION)
                {
                    if (!this.RewritePcsPacket(packet, plan, objectSizes, adjustment, report))
                    {
                        return false;
                    }
                }
                else if (segmentType == PgsSubtitleUtils.SEGMENT_WINDOW)
                {
                    if (!this.RewriteWdsPacket(packet, plan, adjustment, report))
                    {
                        return false;
                    }
                }

                output.Write(packet, 0, packet.Length);
                pos += packetLength;
            }

            return true;
        }

        /// <summary>
        /// Raccoglie le dimensioni oggetto dai segmenti ODS del display-set
        /// </summary>
        /// <param name="data">Buffer SUP</param>
        /// <param name="start">Offset iniziale display-set</param>
        /// <param name="end">Offset finale display-set</param>
        /// <returns>Dimensioni note per object id</returns>
        private Dictionary<int, PgsObjectSize> CollectObjectSizes(byte[] data, int start, int end)
        {
            Dictionary<int, PgsObjectSize> result = new Dictionary<int, PgsObjectSize>();
            int pos = start;
            int packetLength;
            int segmentLength;
            int payload;
            int objectId;
            int sequenceFlags;
            int width;
            int height;

            while (pos < end && PgsSubtitleUtils.TryGetPacketLength(data, pos, out packetLength) && pos + packetLength <= end)
            {
                if (data[pos + 10] == PgsSubtitleUtils.SEGMENT_OBJECT)
                {
                    segmentLength = PgsSubtitleUtils.ReadUInt16BigEndian(data, pos + 11);
                    payload = pos + PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE;
                    if (segmentLength >= 11 && payload + 11 <= data.Length)
                    {
                        objectId = PgsSubtitleUtils.ReadUInt16BigEndian(data, payload);
                        sequenceFlags = data[payload + 3];
                        if ((sequenceFlags & 0x80) != 0)
                        {
                            width = PgsSubtitleUtils.ReadUInt16BigEndian(data, payload + 7);
                            height = PgsSubtitleUtils.ReadUInt16BigEndian(data, payload + 9);
                            result[objectId] = new PgsObjectSize(width, height);
                        }
                    }
                }

                pos += packetLength;
            }

            return result;
        }

        #endregion

        #region Metodi privati - Clamp display-set

        /// <summary>
        /// Calcola il delta locale necessario a tenere il display-set dentro il canvas finale
        /// </summary>
        /// <param name="data">Buffer SUP completo</param>
        /// <param name="start">Offset iniziale display-set</param>
        /// <param name="end">Offset finale display-set</param>
        /// <param name="plan">Piano canvas/coordinate</param>
        /// <param name="objectSizes">Dimensioni oggetto note</param>
        /// <param name="report">Report aggiornato</param>
        /// <param name="adjustment">Delta locale risolto per il display-set</param>
        /// <returns>True se il delta locale e' applicabile</returns>
        private bool TryResolveDisplaySetAdjustment(byte[] data, int start, int end, PgsCanvasRewritePlan plan, Dictionary<int, PgsObjectSize> objectSizes, PgsCanvasRewriteReport report, out PgsDisplaySetAdjustment adjustment)
        {
            PgsDisplaySetBounds bounds;
            int deltaX = 0;
            int deltaY = 0;
            adjustment = new PgsDisplaySetAdjustment();

            if (!this.TryCollectDisplaySetBounds(data, start, end, plan, objectSizes, report, out bounds))
            {
                return false;
            }

            if (!bounds.HasObjects)
            {
                if (!this.TryCollectWdsBounds(data, start, end, plan, report, out bounds))
                {
                    return false;
                }

                if (!bounds.HasObjects)
                {
                    return true;
                }
            }

            if (bounds.Width > plan.OutputCanvasWidth || bounds.Height > plan.OutputCanvasHeight)
            {
                report.ErrorMessage = "bounding box PCS fuori canvas: " + bounds.Width + "x" + bounds.Height;
                return false;
            }

            if (bounds.Right > plan.OutputCanvasWidth)
            {
                deltaX = plan.OutputCanvasWidth - bounds.Right;
            }
            if (bounds.Left + deltaX < 0)
            {
                deltaX += -(bounds.Left + deltaX);
            }

            if (bounds.Bottom > plan.OutputCanvasHeight)
            {
                deltaY = plan.OutputCanvasHeight - bounds.Bottom;
            }
            if (bounds.Top + deltaY < 0)
            {
                deltaY += -(bounds.Top + deltaY);
            }

            if (bounds.Left + deltaX < 0 || bounds.Right + deltaX > plan.OutputCanvasWidth ||
                bounds.Top + deltaY < 0 || bounds.Bottom + deltaY > plan.OutputCanvasHeight)
            {
                report.ErrorMessage = "bounding box PCS non clampabile";
                return false;
            }

            adjustment.DeltaX = deltaX;
            adjustment.DeltaY = deltaY;
            if (deltaX != 0 || deltaY != 0)
            {
                this.UpdateClampReport(deltaX, deltaY, report);
            }

            return true;
        }

        /// <summary>
        /// Raccoglie la bounding box delle finestre WDS dopo offset globale
        /// </summary>
        private bool TryCollectWdsBounds(byte[] data, int start, int end, PgsCanvasRewritePlan plan, PgsCanvasRewriteReport report, out PgsDisplaySetBounds bounds)
        {
            int pos = start;
            int packetLength;
            int segmentLength;
            int payload;
            int windowCount;
            int windowPos;
            int x;
            int y;
            int width;
            int height;

            bounds = new PgsDisplaySetBounds();
            while (pos < end && PgsSubtitleUtils.TryGetPacketLength(data, pos, out packetLength) && pos + packetLength <= end)
            {
                if (data[pos + 10] == PgsSubtitleUtils.SEGMENT_WINDOW)
                {
                    segmentLength = PgsSubtitleUtils.ReadUInt16BigEndian(data, pos + 11);
                    payload = pos + PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE;
                    if (segmentLength < 1 || payload + segmentLength > data.Length)
                    {
                        report.ErrorMessage = "WDS PGS troppo corto";
                        return false;
                    }

                    windowCount = data[payload];
                    windowPos = payload + 1;
                    for (int i = 0; i < windowCount; i++)
                    {
                        if (windowPos + 9 > payload + segmentLength)
                        {
                            report.ErrorMessage = "lista finestre WDS incompleta";
                            return false;
                        }

                        x = PgsSubtitleUtils.ReadUInt16BigEndian(data, windowPos + 1) + plan.OffsetX;
                        y = PgsSubtitleUtils.ReadUInt16BigEndian(data, windowPos + 3) + plan.OffsetY;
                        width = PgsSubtitleUtils.ReadUInt16BigEndian(data, windowPos + 5);
                        height = PgsSubtitleUtils.ReadUInt16BigEndian(data, windowPos + 7);
                        bounds.Include(x, y, x + width, y + height);
                        windowPos += 9;
                    }
                }

                pos += packetLength;
            }

            return true;
        }

        /// <summary>
        /// Raccoglie la bounding box degli oggetti PCS dopo offset globale
        /// </summary>
        private bool TryCollectDisplaySetBounds(byte[] data, int start, int end, PgsCanvasRewritePlan plan, Dictionary<int, PgsObjectSize> objectSizes, PgsCanvasRewriteReport report, out PgsDisplaySetBounds bounds)
        {
            int pos = start;
            int packetLength;
            int segmentLength;
            int payload;
            int objectCount;
            int objectPos;
            int objectId;
            int objectFlags;
            int x;
            int y;
            PgsObjectSize objectSize;

            bounds = new PgsDisplaySetBounds();
            while (pos < end && PgsSubtitleUtils.TryGetPacketLength(data, pos, out packetLength) && pos + packetLength <= end)
            {
                if (data[pos + 10] == PgsSubtitleUtils.SEGMENT_PRESENTATION)
                {
                    segmentLength = PgsSubtitleUtils.ReadUInt16BigEndian(data, pos + 11);
                    payload = pos + PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE;
                    if (segmentLength < 11 || payload + segmentLength > data.Length)
                    {
                        report.ErrorMessage = "PCS PGS troppo corto";
                        return false;
                    }

                    objectCount = data[payload + 10];
                    objectPos = payload + 11;
                    for (int i = 0; i < objectCount; i++)
                    {
                        if (objectPos + 8 > payload + segmentLength)
                        {
                            report.ErrorMessage = "lista oggetti PCS incompleta";
                            return false;
                        }

                        objectId = PgsSubtitleUtils.ReadUInt16BigEndian(data, objectPos);
                        objectFlags = data[objectPos + 3];
                        x = PgsSubtitleUtils.ReadUInt16BigEndian(data, objectPos + 4) + plan.OffsetX;
                        y = PgsSubtitleUtils.ReadUInt16BigEndian(data, objectPos + 6) + plan.OffsetY;
                        objectSizes.TryGetValue(objectId, out objectSize);
                        if (objectSize != null)
                        {
                            bounds.Include(x, y, x + objectSize.Width, y + objectSize.Height);
                        }
                        else
                        {
                            bounds.Include(x, y, x + 1, y + 1);
                        }

                        objectPos += 8;
                        if ((objectFlags & 0x40) != 0)
                        {
                            if (objectPos + 8 > payload + segmentLength)
                            {
                                report.ErrorMessage = "crop oggetto PCS incompleto";
                                return false;
                            }

                            objectPos += 8;
                        }
                    }
                }

                pos += packetLength;
            }

            return true;
        }

        /// <summary>
        /// Aggiorna report clamp display-set
        /// </summary>
        private void UpdateClampReport(int deltaX, int deltaY, PgsCanvasRewriteReport report)
        {
            report.DisplaySetsClamped++;
            if (deltaX < 0)
            {
                report.MaxClampLeftPx = Math.Max(report.MaxClampLeftPx, -deltaX);
            }
            else if (deltaX > 0)
            {
                report.MaxClampRightPx = Math.Max(report.MaxClampRightPx, deltaX);
            }

            if (deltaY < 0)
            {
                report.MaxClampUpPx = Math.Max(report.MaxClampUpPx, -deltaY);
            }
            else if (deltaY > 0)
            {
                report.MaxClampDownPx = Math.Max(report.MaxClampDownPx, deltaY);
            }
        }

        #endregion

        #region Metodi privati - Segmenti PGS

        /// <summary>
        /// Riscrive un Presentation Composition Segment
        /// </summary>
        /// <param name="packet">Packet PCS</param>
        /// <param name="plan">Piano canvas/coordinate</param>
        /// <param name="objectSizes">Dimensioni oggetto note</param>
        /// <param name="adjustment">Delta locale del display-set</param>
        /// <param name="report">Report aggiornato</param>
        /// <returns>True se il PCS resta coerente</returns>
        private bool RewritePcsPacket(byte[] packet, PgsCanvasRewritePlan plan, Dictionary<int, PgsObjectSize> objectSizes, PgsDisplaySetAdjustment adjustment, PgsCanvasRewriteReport report)
        {
            int segmentLength = PgsSubtitleUtils.ReadUInt16BigEndian(packet, 11);
            int payload = PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE;
            int oldWidth;
            int oldHeight;
            int objectCount;
            int pos;
            int objectId;
            int objectFlags;
            int x;
            int y;
            int newX;
            int newY;
            PgsObjectSize objectSize;

            if (segmentLength < 11 || payload + segmentLength > packet.Length)
            {
                report.ErrorMessage = "PCS PGS troppo corto";
                return false;
            }

            oldWidth = PgsSubtitleUtils.ReadUInt16BigEndian(packet, payload);
            oldHeight = PgsSubtitleUtils.ReadUInt16BigEndian(packet, payload + 2);
            if (oldWidth != plan.InputCanvasWidth || oldHeight != plan.InputCanvasHeight)
            {
                report.ErrorMessage = "canvas PCS inatteso " + oldWidth + "x" + oldHeight;
                return false;
            }

            PgsSubtitleUtils.WriteUInt16BigEndian(packet, payload, plan.OutputCanvasWidth);
            PgsSubtitleUtils.WriteUInt16BigEndian(packet, payload + 2, plan.OutputCanvasHeight);
            objectCount = packet[payload + 10];
            pos = payload + 11;

            for (int i = 0; i < objectCount; i++)
            {
                if (pos + 8 > payload + segmentLength)
                {
                    report.ErrorMessage = "lista oggetti PCS incompleta";
                    return false;
                }

                objectId = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos);
                objectFlags = packet[pos + 3];
                x = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos + 4);
                y = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos + 6);
                newX = x + plan.OffsetX + adjustment.DeltaX;
                newY = y + plan.OffsetY + adjustment.DeltaY;
                objectSizes.TryGetValue(objectId, out objectSize);
                if (!this.ValidateObjectBounds(newX, newY, objectSize, plan, report))
                {
                    return false;
                }

                PgsSubtitleUtils.WriteUInt16BigEndian(packet, pos + 4, newX);
                PgsSubtitleUtils.WriteUInt16BigEndian(packet, pos + 6, newY);
                report.ObjectCoordinatesRewritten++;
                pos += 8;
                if ((objectFlags & 0x40) != 0)
                {
                    if (pos + 8 > payload + segmentLength)
                    {
                        report.ErrorMessage = "crop oggetto PCS incompleto";
                        return false;
                    }

                    pos += 8;
                }
            }

            report.PcsSegments++;
            return true;
        }

        /// <summary>
        /// Riscrive un Window Definition Segment
        /// </summary>
        /// <param name="packet">Packet WDS</param>
        /// <param name="plan">Piano canvas/coordinate</param>
        /// <param name="adjustment">Delta locale del display-set</param>
        /// <param name="report">Report aggiornato</param>
        /// <returns>True se il WDS resta coerente</returns>
        private bool RewriteWdsPacket(byte[] packet, PgsCanvasRewritePlan plan, PgsDisplaySetAdjustment adjustment, PgsCanvasRewriteReport report)
        {
            int segmentLength = PgsSubtitleUtils.ReadUInt16BigEndian(packet, 11);
            int payload = PgsSubtitleUtils.SUP_PACKET_HEADER_SIZE;
            int windowCount;
            int pos;
            int x;
            int y;
            int width;
            int height;
            int baseX;
            int baseY;
            int newX;
            int newY;

            if (segmentLength < 1 || payload + segmentLength > packet.Length)
            {
                report.ErrorMessage = "WDS PGS troppo corto";
                return false;
            }

            windowCount = packet[payload];
            pos = payload + 1;
            for (int i = 0; i < windowCount; i++)
            {
                if (pos + 9 > payload + segmentLength)
                {
                    report.ErrorMessage = "lista finestre WDS incompleta";
                    return false;
                }

                x = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos + 1);
                y = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos + 3);
                width = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos + 5);
                height = PgsSubtitleUtils.ReadUInt16BigEndian(packet, pos + 7);
                baseX = x + plan.OffsetX;
                baseY = y + plan.OffsetY;
                if (x == 0 && y == 0 && width == plan.InputCanvasWidth && height == plan.InputCanvasHeight)
                {
                    newX = 0;
                    newY = 0;
                    width = plan.OutputCanvasWidth;
                    height = plan.OutputCanvasHeight;
                }
                else if (baseX == 0 && baseY == 0 && width == plan.OutputCanvasWidth && height == plan.OutputCanvasHeight)
                {
                    newX = 0;
                    newY = 0;
                }
                else
                {
                    newX = baseX + adjustment.DeltaX;
                    newY = baseY + adjustment.DeltaY;
                }

                if (!this.ValidateRectBounds(newX, newY, width, height, plan, report, "WDS fuori canvas"))
                {
                    return false;
                }

                PgsSubtitleUtils.WriteUInt16BigEndian(packet, pos + 1, newX);
                PgsSubtitleUtils.WriteUInt16BigEndian(packet, pos + 3, newY);
                PgsSubtitleUtils.WriteUInt16BigEndian(packet, pos + 5, width);
                PgsSubtitleUtils.WriteUInt16BigEndian(packet, pos + 7, height);
                report.WindowDefinitionsRewritten++;
                pos += 9;
            }

            report.WdsSegments++;
            return true;
        }

        #endregion

        #region Metodi privati - Validazione

        /// <summary>
        /// Valida bounds di un oggetto PCS
        /// </summary>
        private bool ValidateObjectBounds(int x, int y, PgsObjectSize size, PgsCanvasRewritePlan plan, PgsCanvasRewriteReport report)
        {
            if (size != null)
            {
                return this.ValidateRectBounds(x, y, size.Width, size.Height, plan, report, "oggetto PCS fuori canvas");
            }

            if (x < 0 || y < 0 || x >= plan.OutputCanvasWidth || y >= plan.OutputCanvasHeight)
            {
                report.ErrorMessage = "coordinate PCS fuori canvas";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Valida bounds di un rettangolo nel canvas finale
        /// </summary>
        private bool ValidateRectBounds(int x, int y, int width, int height, PgsCanvasRewritePlan plan, PgsCanvasRewriteReport report, string errorMessage)
        {
            if (x < 0 || y < 0 || width <= 0 || height <= 0 ||
                x + width > plan.OutputCanvasWidth ||
                y + height > plan.OutputCanvasHeight)
            {
                report.ErrorMessage = errorMessage;
                return false;
            }

            return true;
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Delta locale applicato a un display-set
        /// </summary>
        private class PgsDisplaySetAdjustment
        {
            /// <summary>
            /// Delta X locale
            /// </summary>
            public int DeltaX { get; set; }

            /// <summary>
            /// Delta Y locale
            /// </summary>
            public int DeltaY { get; set; }
        }

        /// <summary>
        /// Bounding box degli oggetti PCS di un display-set
        /// </summary>
        private class PgsDisplaySetBounds
        {
            /// <summary>
            /// True se sono stati inclusi oggetti
            /// </summary>
            public bool HasObjects { get; private set; }

            /// <summary>
            /// Coordinata sinistra inclusiva
            /// </summary>
            public int Left { get; private set; }

            /// <summary>
            /// Coordinata superiore inclusiva
            /// </summary>
            public int Top { get; private set; }

            /// <summary>
            /// Coordinata destra esclusiva
            /// </summary>
            public int Right { get; private set; }

            /// <summary>
            /// Coordinata inferiore esclusiva
            /// </summary>
            public int Bottom { get; private set; }

            /// <summary>
            /// Larghezza bounding box
            /// </summary>
            public int Width
            {
                get { return this.Right - this.Left; }
            }

            /// <summary>
            /// Altezza bounding box
            /// </summary>
            public int Height
            {
                get { return this.Bottom - this.Top; }
            }

            /// <summary>
            /// Include un rettangolo nella bounding box
            /// </summary>
            public void Include(int left, int top, int right, int bottom)
            {
                if (!this.HasObjects)
                {
                    this.Left = left;
                    this.Top = top;
                    this.Right = right;
                    this.Bottom = bottom;
                    this.HasObjects = true;
                    return;
                }

                this.Left = Math.Min(this.Left, left);
                this.Top = Math.Min(this.Top, top);
                this.Right = Math.Max(this.Right, right);
                this.Bottom = Math.Max(this.Bottom, bottom);
            }
        }

        /// <summary>
        /// Dimensione oggetto PGS
        /// </summary>
        private class PgsObjectSize
        {
            /// <summary>
            /// Costruttore
            /// </summary>
            public PgsObjectSize(int width, int height)
            {
                this.Width = width;
                this.Height = height;
            }

            /// <summary>
            /// Larghezza bitmap
            /// </summary>
            public int Width { get; private set; }

            /// <summary>
            /// Altezza bitmap
            /// </summary>
            public int Height { get; private set; }
        }

        #endregion
    }
}
