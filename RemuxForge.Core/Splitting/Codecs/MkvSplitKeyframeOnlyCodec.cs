using RemuxForge.Core.Models;

namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// Codec che si taglia solo al keyframe, senza ricodifica (VC-1)
    /// </summary>
    public class MkvSplitKeyframeOnlyCodec : MkvSplitVideoCodecBase
    {
        #region Proprietà

        /// <summary>
        /// Nessun taglio al fotogramma
        /// </summary>
        public override MkvSplitExactCutMethod Method => MkvSplitExactCutMethod.None;

        #endregion
    }
}
